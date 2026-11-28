
using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartFleetBE.Constants;
using SmartFleetBE.Model;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/robots")]
[Authorize(
    Roles = AppRoles.Admin + "," +
            AppRoles.WarehouseOperator + "," +
            AppRoles.MaintenanceTechnician)]
public sealed class RobotsController : ControllerBase
{
    private readonly SmartFleetDbContext _db;
    private readonly IConfiguration _configuration;

    public RobotsController(SmartFleetDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    // GET /api/v1/robots
    [HttpGet]
    public async Task<IActionResult> GetRobots(
        CancellationToken ct)
    {
        // Fetch the fleet snapshot in one database round trip. Four sequential
        // queries made every fleet refresh pay the cloud network latency four times.
        var result = await _db.Robots
            .AsNoTracking()
            .AsSingleQuery()
            .OrderBy(r => r.RobotId)
            .Select(r => new RobotInfo
            {
                RobotId = r.RobotId,
                RobotCode = r.RobotCode,
                Model = r.Model,
                ConnectionStatus = r.ConnectionStatus,
                OperationalStatus = r.OperationalStatus,
                BatteryPercent = r.BatteryPercent,
                IsActive = r.IsActive,
                HasActiveAssignment = _db.TaskAssignments.Any(a =>
                    a.RobotId == r.RobotId && a.AssignmentStatus == "ACTIVE"),
                HasActiveMaintenance = _db.RobotMaintenanceRecords.Any(m =>
                    m.RobotId == r.RobotId &&
                    (m.MaintenanceStatus == "IN_PROGRESS" || m.MaintenanceStatus == "SCHEDULED")),
                OpenIncidents = _db.RobotIncidents
                    .Where(i => i.RobotId == r.RobotId && !i.IsResolved)
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => new RobotIncidentInfo
                    {
                        IncidentId = i.IncidentId,
                        RobotId = i.RobotId,
                        IncidentType = i.IncidentType,
                        Severity = i.Severity,
                        Description = i.Description,
                        CreatedAt = i.CreatedAt
                    }).ToList()
            })
            .ToListAsync(ct);

        return Ok(result);
    }

    // PATCH /api/v1/robots/{robotId}/operational-status
    [HttpPatch("{robotId:int}/operational-status")]
    [Authorize(Roles = AppRoles.AdminOrMaintenanceTechnician)]
    public async Task<IActionResult> ChangeOperationalStatus(
        int robotId,
        [FromBody] ChangeRobotStatusRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out int userId))
            return Unauthorized();

        var target = request.OperationalStatus
            ?.Trim().ToUpperInvariant();

        var reason = request.Reason?.Trim() ?? "";

        if (target is not ("ERROR" or "MAINTENANCE" or "AVAILABLE"))
            return BadRequest(new
            {
                message = "Allowed statuses: ERROR, MAINTENANCE, AVAILABLE."
            });

        if (reason.Length < 5 || reason.Length > 500)
            return BadRequest(new
            {
                message = "Reason must contain 5 to 500 characters."
            });

        try
        {
            await using var tx = await _db.Database
                .BeginTransactionAsync(
                    IsolationLevel.Serializable, ct);

            var robot = await _db.Robots
                .SingleOrDefaultAsync(
                    r => r.RobotId == robotId, ct);

            if (robot == null)
                return NotFound(new { message = "Robot not found." });

            if (!robot.IsActive)
                return Conflict(new
                {
                    message = "Inactive robot cannot be released."
                });

            if (robot.OperationalStatus == target)
                return Ok(new
                {
                    robotId,
                    operationalStatus = target,
                    message = "Robot is already in this status."
                });

            // Never overwrite an executing assignment.
            bool hasActiveAssignment =
                await _db.TaskAssignments.AnyAsync(
                    a => a.RobotId == robotId &&
                         a.AssignmentStatus == "ACTIVE",
                    ct);

            if (hasActiveAssignment ||
                robot.OperationalStatus == "BUSY")
            {
                return Conflict(new
                {
                    message =
                        "Robot has an active mission or is BUSY. " +
                        "Stop the physical robot and safely resolve " +
                        "the mission before changing operational status."
                });
            }
            if (await _db.ChargingJobs.AnyAsync(j => j.RobotId == robotId && j.State != "COMPLETED", ct))
                return Conflict(new { message = "Robot has a reserved charging workflow. Use its Pause/recovery controls; do not override device availability." });

            if (robot.OperationalStatus == "CHARGING")
            {
                return Conflict(new
                {
                    message =
                        "Robot is CHARGING. End the charging " +
                        "workflow before changing status."
                });
            }

            var now = DateTime.UtcNow;
            var oldStatus = robot.OperationalStatus;

            // ---------------------------------------------
            // REPORT FAULT: set ERROR + create incident
            // ---------------------------------------------
            if (target == "ERROR")
            {
                _db.RobotIncidents.Add(new RobotIncident
                {
                    RobotId = robotId,
                    IncidentType = "OTHER",
                    Severity = "ERROR",
                    Description = reason,
                    IsResolved = false,
                    CreatedAt = now
                });
            }

            // ---------------------------------------------
            // START MAINTENANCE
            // ---------------------------------------------
            if (target == "MAINTENANCE")
            {
                var maintenance =
                    await _db.RobotMaintenanceRecords
                        .Where(m => m.RobotId == robotId &&
                            (m.MaintenanceStatus == "SCHEDULED" ||
                             m.MaintenanceStatus == "IN_PROGRESS"))
                        .OrderByDescending(m => m.CreatedAt)
                        .FirstOrDefaultAsync(ct);

                if (maintenance != null)
                {
                    maintenance.MaintenanceStatus = "IN_PROGRESS";
                    maintenance.StartedAt ??= now;
                }
                else
                {
                    _db.RobotMaintenanceRecords.Add(
                        new RobotMaintenanceRecord
                        {
                            RobotId = robotId,
                            MaintenanceType = "CORRECTIVE",
                            MaintenanceStatus = "IN_PROGRESS",
                            Description = reason,
                            CreatedBy = userId,
                            CreatedAt = now,
                            StartedAt = now
                        });
                }
            }

            // ---------------------------------------------
            // RETURN ROBOT TO AVAILABLE
            // ---------------------------------------------
            if (target == "AVAILABLE")
            {
                if (robot.ConnectionStatus != "ONLINE" || !RobotPresencePolicy.IsFresh(robot, now, _configuration))
                {
                    return Conflict(new
                    {
                        message =
                            "Robot must be ONLINE with fresh heartbeat/telemetry before becoming AVAILABLE."
                    });
                }

                if (robot.BatteryPercent is null ||
                    robot.BatteryPercent < 20m)
                {
                    return Conflict(new
                    {
                        message =
                            "Robot must have at least 20% battery."
                    });
                }

                bool unresolvedIncident =
                    await _db.RobotIncidents.AnyAsync(
                        i => i.RobotId == robotId &&
                             !i.IsResolved, ct);

                if (unresolvedIncident)
                {
                    return Conflict(new
                    {
                        message =
                            "Resolve all open incidents before " +
                            "returning the robot to AVAILABLE."
                    });
                }

                var maintenanceRecords =
                    await _db.RobotMaintenanceRecords
                        .Where(m => m.RobotId == robotId &&
                            (m.MaintenanceStatus == "IN_PROGRESS" ||
                             m.MaintenanceStatus == "SCHEDULED"))
                        .ToListAsync(ct);

                foreach (var maintenance in maintenanceRecords)
                {
                    if (maintenance.MaintenanceStatus == "IN_PROGRESS")
                    {
                        maintenance.MaintenanceStatus = "COMPLETED";
                        maintenance.CompletedAt = now;
                        maintenance.CompletedBy = userId;
                    }
                    else
                    {
                        // Explicit release cancels any remaining
                        // scheduled maintenance for this robot.
                        maintenance.MaintenanceStatus = "CANCELLED";
                    }
                }
            }

            robot.OperationalStatus = target;
            robot.UpdatedAt = now;

            var historyReason =
                $"Maintenance user {userId}: {reason}";

            _db.RobotStatusHistories.Add(
                new RobotStatusHistory
                {
                    RobotId = robotId,
                    PreviousOperationalStatus = oldStatus,
                    NewOperationalStatus = target,
                    Reason = historyReason.Length <= 500
                        ? historyReason
                        : historyReason[..500],
                    ChangedAt = now
                });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(new
            {
                robotId,
                robotCode = robot.RobotCode,
                previousStatus = oldStatus,
                operationalStatus = target,
                message = "Robot status updated successfully."
            });
        }
        catch (Exception ex) when (IsDbConflict(ex))
        {
            return Conflict(new
            {
                message =
                    "Robot state changed concurrently. " +
                    "Refresh and try again."
            });
        }
    }

    // POST /api/v1/robots/{robotId}/incidents/{incidentId}/resolve
    [HttpPost("{robotId:int}/incidents/{incidentId:long}/resolve")]
    [Authorize(Roles = AppRoles.AdminOrMaintenanceTechnician)]
    public async Task<IActionResult> ResolveIncident(
        int robotId,
        long incidentId,
        [FromBody] ResolveRobotIncidentRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out int userId))
            return Unauthorized();

        var note = request.ResolutionNote?.Trim() ?? "";

        if (note.Length < 5 || note.Length > 1000)
            return BadRequest(new
            {
                message =
                    "Resolution note must contain 5 to 1000 characters."
            });

        try
        {
            await using var tx = await _db.Database
                .BeginTransactionAsync(
                    IsolationLevel.Serializable, ct);

            var incident = await _db.RobotIncidents
                .SingleOrDefaultAsync(
                    i => i.IncidentId == incidentId &&
                         i.RobotId == robotId, ct);

            if (incident == null)
                return NotFound(new
                {
                    message = "Incident not found."
                });

            if (incident.IsResolved)
                return Conflict(new
                {
                    message = "Incident was already resolved."
                });

            incident.IsResolved = true;
            incident.ResolvedAt = DateTime.UtcNow;
            incident.ResolvedBy = userId;
            incident.ResolutionNote = note;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(new
            {
                incidentId,
                robotId,
                isResolved = true,
                message = "Incident resolved successfully."
            });
        }
        catch (Exception ex) when (IsDbConflict(ex))
        {
            return Conflict(new
            {
                message = "Incident changed concurrently. Retry."
            });
        }
    }

    private bool TryGetUserId(out int userId)
    {
        return int.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out userId);
    }

    private static bool IsDbConflict(Exception ex)
    {
        for (Exception? current = ex;
             current != null;
             current = current.InnerException)
        {
            if (current is PostgresException pg &&
                (pg.SqlState == "40001" ||
                 pg.SqlState == "40P01"))
                return true;
        }

        return false;
    }
}

public sealed class ChangeRobotStatusRequest
{
    public string OperationalStatus { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ResolveRobotIncidentRequest
{
    public string ResolutionNote { get; set; } = "";
}

public sealed class RobotInfo
{
    public int RobotId { get; set; }
    public string RobotCode { get; set; } = "";
    public string Model { get; set; } = "";
    public string ConnectionStatus { get; set; } = "";
    public string OperationalStatus { get; set; } = "";
    public decimal? BatteryPercent { get; set; }
    public bool IsActive { get; set; }
    public bool HasActiveAssignment { get; set; }
    public bool HasActiveMaintenance { get; set; }
    public List<RobotIncidentInfo> OpenIncidents { get; set; } = new();
}

public sealed class RobotIncidentInfo
{
    public long IncidentId { get; set; }
    public int RobotId { get; set; }
    public string IncidentType { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}
