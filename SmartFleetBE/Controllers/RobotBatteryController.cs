using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

/// <summary>
/// Read battery snapshots and optionally set a manual reading for maintenance/testing.
/// Real robot battery readings should arrive on MQTT telemetry, not from a browser PUT.
/// </summary>
[ApiController]
[Route("api/v1/robots")]
[Authorize(Roles = AppRoles.Admin + "," +
                   AppRoles.WarehouseOperator + "," +
                   AppRoles.MaintenanceTechnician)]
public sealed class RobotBatteryController(
    SmartFleetDbContext db,
    FleetRealtimePublisher realtime,
    IConfiguration configuration,
    ILogger<RobotBatteryController> logger) : ControllerBase
{
    // GET /api/v1/robots/batteries - initial state for the entire dashboard.
    [HttpGet("batteries")]
    public async Task<ActionResult<IReadOnlyList<RobotBatteryResponse>>>
        GetAllBatteries(CancellationToken ct)
    {
        var robots = await db.Robots.AsNoTracking()
            .OrderBy(r => r.RobotId)
            .ToListAsync(ct);

        int staleAfterSeconds = GetStaleAfterSeconds();
        var now = DateTime.UtcNow;

        return Ok(robots.Select(robot =>
            RobotBatteryResponse.FromRobot(robot, now, staleAfterSeconds))
            .ToList());
    }

    // GET /api/v1/robots/1/battery - one robot's latest persisted battery.
    [HttpGet("{robotId:int}/battery")]
    public async Task<ActionResult<RobotBatteryResponse>>
        GetBattery(int robotId, CancellationToken ct)
    {
        var robot = await db.Robots.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RobotId == robotId, ct);

        if (robot is null)
            return NotFound(new { message = "Robot not found." });

        return Ok(RobotBatteryResponse.FromRobot(
            robot, DateTime.UtcNow, GetStaleAfterSeconds()));
    }

    // PUT is restricted to Administrator / Maintenance Technician for manual testing.
    // It DOES NOT change ONLINE/OFFLINE or the last physical telemetry timestamp.
    [HttpPut("{robotId:int}/battery")]
    [Authorize(Roles = AppRoles.AdminOrMaintenanceTechnician)]
    public async Task<ActionResult<RobotBatteryResponse>>
        UpdateBattery(int robotId,
            [FromBody] UpdateRobotBatteryRequest request,
            CancellationToken ct)
    {
        if (!request.BatteryPercent.HasValue ||
            request.BatteryPercent.Value is < 0m or > 100m)
        {
            return BadRequest(new
            {
                message = "batteryPercent must be between 0 and 100."
            });
        }

        if (request.BatteryVoltage is < 0m or > 999m)
        {
            return BadRequest(new
            {
                message = "batteryVoltage must be between 0 and 999."
            });
        }

        var robot = await db.Robots
            .SingleOrDefaultAsync(r => r.RobotId == robotId, ct);

        if (robot is null)
            return NotFound(new { message = "Robot not found." });

        if (!robot.IsActive)
            return Conflict(new { message = "Robot is inactive." });

        robot.BatteryPercent = request.BatteryPercent.Value;

        if (request.BatteryVoltage.HasValue)
            robot.BatteryVoltage = request.BatteryVoltage.Value;

        robot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var snapshot = RobotBatteryResponse.FromRobot(
            robot, DateTime.UtcNow, GetStaleAfterSeconds());

        // Database save must succeed even if no SignalR client is connected.
        try
        {
            await realtime.BatteryUpdatedAsync(snapshot, ct);
            await realtime.RobotUpdatedAsync(robot.RobotCode, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Battery updated for robot {RobotCode}, but SignalR push failed.",
                robot.RobotCode);
        }

        return Ok(snapshot);
    }

    private int GetStaleAfterSeconds() => Math.Clamp(
        configuration.GetValue("Battery:StaleAfterSeconds", 45), 10, 3600);
}

public sealed class UpdateRobotBatteryRequest
{
    public decimal? BatteryPercent { get; set; }
    public decimal? BatteryVoltage { get; set; }
}
