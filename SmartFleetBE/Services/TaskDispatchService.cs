
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Model;
using SmartFleetBE.Services.Interfaces;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Services;

public sealed class TaskDispatchService : ITaskDispatchService
{
    private const decimal MinimumBatteryPercent = 20m;

    private readonly SmartFleetDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly FleetRealtimePublisher _realtime;
    private readonly ILogger<TaskDispatchService> _logger;

    public TaskDispatchService(
        SmartFleetDbContext db,
        IConfiguration configuration,
        ILogger<TaskDispatchService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    // =====================================================
    // GET ROBOTS AVAILABLE FOR MANUAL ASSIGNMENT
    // =====================================================

    public async Task<
        TransportTaskServiceResult<
            IReadOnlyCollection<AvailableRobotResponse>>>
        GetAvailableRobotsAsync(
            long taskId,
            CancellationToken cancellationToken = default, bool includeIneligible = false)
    {
        var task = await _db.TransportTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.TaskId == taskId,
                cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<
                IReadOnlyCollection<AvailableRobotResponse>>
                .NotFound($"Task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Queued,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                IReadOnlyCollection<AvailableRobotResponse>>
                .Conflict(
                    $"Task must be QUEUED. Current status: {task.TaskStatus}.");
        }

        if (task.ScheduledTime > DateTime.UtcNow || await _db.TaskInterventions.AnyAsync(c =>
            c.TaskId == taskId && (c.Paused || c.State != "NORMAL"), cancellationToken))
            return TransportTaskServiceResult<IReadOnlyCollection<AvailableRobotResponse>>
                .Conflict("Task is waiting for its scheduled/recovery time or has a safety hold.");

        if (task.PayloadWeightKg is not > 0m)
        {
            return TransportTaskServiceResult<
                IReadOnlyCollection<AvailableRobotResponse>>
                .Conflict("Task payload weight must be greater than zero.");
        }

        decimal weightKg = task.PayloadWeightKg.Value;

        decimal verifiedLimitKg =
            _configuration.GetValue<decimal>(
                "AutoDispatch:VerifiedPayloadLimitKg");

        if (verifiedLimitKg <= 0m ||
            weightKg > verifiedLimitKg)
        {
            return TransportTaskServiceResult<
                IReadOnlyCollection<AvailableRobotResponse>>
                .Conflict(
                    "Task exceeds the verified payload safety limit, " +
                    "or the limit has not been configured.");
        }

        _logger.LogInformation("Task {TaskId}: warehouseId={WarehouseId}, payloadKg={PayloadKg}", 
            taskId, task.WarehouseId, weightKg);

        // Show exclusion reasons when requested; never silently treat a DB row as a live robot.
        var allRobots = await _db.Robots.AsNoTracking().OrderBy(r => r.RobotCode).ToListAsync(cancellationToken);
        var busy = (await _db.TaskAssignments.Where(a => a.AssignmentStatus == "ACTIVE")
            .Select(a => a.RobotId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var failed = (await _db.TaskAssignments.Where(a => a.TaskId == taskId && a.AssignmentStatus == "FAILED")
            .Select(a => a.RobotId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var maintenance = (await _db.RobotMaintenanceRecords.Where(m =>
            m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS")
            .Select(m => m.RobotId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var incidents = (await _db.RobotIncidents.Where(i => !i.IsResolved &&
            (i.Severity == "HIGH" || i.Severity == "CRITICAL"))
            .Select(i => i.RobotId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var now = DateTime.UtcNow;
        var robots = new List<AvailableRobotResponse>();
        foreach (var robot in allRobots)
        {
            var reasons = new List<string>();
            if (robot.WarehouseId != task.WarehouseId) reasons.Add("Different warehouse");
            if (!robot.IsActive) reasons.Add("Robot inactive");
            if (robot.RobotType != "PHYSICAL") reasons.Add("Robot type is not PHYSICAL");
            if (robot.ConnectionStatus != "ONLINE") reasons.Add("Robot OFFLINE");
            if (robot.OperationalStatus != "AVAILABLE") reasons.Add($"Robot status: {robot.OperationalStatus}");
            if (!RobotPresencePolicy.IsFresh(robot, now, _configuration)) reasons.Add("Heartbeat/telemetry missing or stale");
            if (robot.BatteryPercent == null || robot.BatteryPercent < MinimumBatteryPercent) reasons.Add("Battery must be at least 20%");
            if (robot.MaxPayloadWeightKg < weightKg) reasons.Add("Insufficient payload capacity");
            if (busy.Contains(robot.RobotId)) reasons.Add("Has ACTIVE assignment");
            if (failed.Contains(robot.RobotId)) reasons.Add("Previously failed this task");
            if (maintenance.Contains(robot.RobotId)) reasons.Add("Maintenance scheduled or in progress");
            if (incidents.Contains(robot.RobotId)) reasons.Add("Unresolved serious incident");
            var eligible = reasons.Count == 0;
            if (!includeIneligible && !eligible) continue;
            robots.Add(new AvailableRobotResponse {
                RobotId = robot.RobotId, RobotCode = robot.RobotCode,
                BatteryPercent = robot.BatteryPercent, ConnectionStatus = robot.ConnectionStatus,
                OperationalStatus = robot.OperationalStatus, IsEligible = eligible,
                IneligibilityReason = eligible ? null : string.Join("; ", reasons),
                LastHeartbeatTime = robot.LastHeartbeatTime, LastTelemetryTime = robot.LastTelemetryTime
            });
        }
        robots = robots.OrderByDescending(r => r.IsEligible).ThenByDescending(r => r.BatteryPercent)
            .ThenBy(r => r.RobotId).ToList();
        return TransportTaskServiceResult<
            IReadOnlyCollection<AvailableRobotResponse>>
            .Success(robots);
    }

    // =====================================================
    // ASSIGN TASK MANUALLY
    // =====================================================

    // Retry the ENTIRE manual assignment transaction after a PostgreSQL
    // serialization failure, not just SaveChangesAsync. The inner method owns
    // one Serializable transaction; on failure it is disposed/rolled back
    // before the next attempt. No DB schema change is required.
    public async Task<TransportTaskServiceResult<ManualAssignTaskResponse>>
        AssignManualAsync(
            long taskId,
            int robotId,
            int operatorUserId,
            CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await AssignManualCoreAsync(
                    taskId, robotId, operatorUserId, cancellationToken);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                IsManualAssignmentSerializationConflict(ex))
            {
                // Core method's await using has rolled back and disposed the
                // transaction by the time control reaches this catch block.
                // Detached entities prevent replaying changes from a failed try.
                _db.ChangeTracker.Clear();

                if (attempt == maxAttempts)
                {
                    _logger.LogWarning(ex,
                        "Manual assignment of Task {TaskId} to Robot {RobotId} " +
                        "failed after {Attempts} serialization retries",
                        taskId, robotId, attempt);
                    return TransportTaskServiceResult<ManualAssignTaskResponse>
                        .Conflict("Robot telemetry or another transaction updated " +
                                  "the robot concurrently. Please retry assignment.");
                }

                _logger.LogWarning(
                    "PostgreSQL concurrency conflict while assigning Task {TaskId} " +
                    "to Robot {RobotId} (attempt {Attempt}/{MaxAttempts}); " +
                    "retrying the entire transaction",
                    taskId, robotId, attempt, maxAttempts);

                await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        125 * attempt + Random.Shared.Next(75, 250)),
                    cancellationToken);
            }
        }

        return TransportTaskServiceResult<ManualAssignTaskResponse>
            .Conflict("Could not assign robot after retrying the transaction.");
    }

    private static bool IsManualAssignmentSerializationConflict(Exception ex)
    {
        for (Exception? current = ex; current != null;
             current = current.InnerException)
        {
            if (current is Npgsql.PostgresException pg &&
                (pg.SqlState == "40001" || pg.SqlState == "40P01"))
                return true;
        }
        return false;
    }

    private async Task<
        TransportTaskServiceResult<ManualAssignTaskResponse>>
        AssignManualCoreAsync(
            long taskId,
            int robotId,
            int operatorUserId,
            CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        // STEP 1: GET TASK

        var task = await _db.TransportTasks
            .SingleOrDefaultAsync(
                t => t.TaskId == taskId,
                cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .NotFound($"Task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Queued,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    $"Task must be QUEUED. Current status: {task.TaskStatus}.");
        }

        if (task.PayloadWeightKg is not > 0m)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    "Task payload weight must be greater than zero.");
        }

        decimal weightKg = task.PayloadWeightKg.Value;

        decimal verifiedLimitKg =
            _configuration.GetValue<decimal>(
                "AutoDispatch:VerifiedPayloadLimitKg");

        if (verifiedLimitKg <= 0m ||
            weightKg > verifiedLimitKg)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    "Payload exceeds the verified safety limit, " +
                    "or the limit has not been configured.");
        }

        if (task.ScheduledTime > DateTime.UtcNow || await _db.TaskInterventions.AnyAsync(c =>
            c.TaskId == taskId && (c.Paused || c.State != "NORMAL"), cancellationToken))
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Task is waiting for its scheduled/recovery time or has a safety hold.");

        if (await _db.TaskAssignments.AnyAsync(a => a.TaskId == taskId && a.RobotId == robotId &&
            a.AssignmentStatus == "FAILED", cancellationToken))
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("This robot previously failed this task. Select another robot.");

        // STEP 2: CHECK TASK IS NOT ALREADY ASSIGNED

        bool taskBusy = await _db.TaskAssignments.AnyAsync(
            a => a.TaskId == taskId &&
                 a.AssignmentStatus == "ACTIVE",
            cancellationToken);

        if (taskBusy)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Task already has an ACTIVE assignment.");
        }

        // STEP 3: GET REQUESTED ROBOT

        var robot = await _db.Robots
            .SingleOrDefaultAsync(
                r => r.RobotId == robotId,
                cancellationToken);

        if (robot is null)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .NotFound($"Robot {robotId} was not found.");
        }

        // STEP 4: VALIDATE ROBOT

        if (!RobotPresencePolicy.IsFresh(robot, DateTime.UtcNow, _configuration))
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot heartbeat/telemetry is missing or stale. Wait for fresh robot data and reload the robot list.");

        if (!robot.IsActive)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot is inactive.");
        }

        if (!string.Equals(
                robot.RobotType,
                "PHYSICAL",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot must be PHYSICAL.");
        }

        if (robot.WarehouseId != task.WarehouseId)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    "Robot and Task must belong to the same warehouse.");
        }

        if (!string.Equals(
                robot.ConnectionStatus,
                "ONLINE",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot is OFFLINE.");
        }

        if (!string.Equals(
                robot.OperationalStatus,
                "AVAILABLE",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    $"Robot is not AVAILABLE: {robot.OperationalStatus}.");
        }

        if (!robot.BatteryPercent.HasValue ||
            robot.BatteryPercent.Value < MinimumBatteryPercent)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    $"Robot must have at least {MinimumBatteryPercent}% battery.");
        }

        // STEP 5: CHECK PAYLOAD CAPACITY

        if (robot.MaxPayloadWeightKg <= 0m ||
            weightKg > robot.MaxPayloadWeightKg)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    $"Payload {weightKg} kg exceeds " +
                    $"robot capacity {robot.MaxPayloadWeightKg} kg.");
        }

        // STEP 6: CHECK MAINTENANCE

        bool inMaintenance =
            await _db.RobotMaintenanceRecords.AnyAsync(
                m => m.RobotId == robotId &&
                     (m.MaintenanceStatus == "SCHEDULED" ||
                      m.MaintenanceStatus == "IN_PROGRESS"),
                cancellationToken);

        if (inMaintenance)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot is under maintenance.");
        }

        // STEP 7: CHECK INCIDENTS

        bool severeIncident = await _db.RobotIncidents.AnyAsync(
            i => i.RobotId == robotId &&
                 !i.IsResolved &&
                 (i.Severity == "HIGH" ||
                  i.Severity == "CRITICAL"),
            cancellationToken);

        if (severeIncident)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    "Robot has an unresolved serious incident.");
        }

        // STEP 8: CHECK ROBOT ACTIVE ASSIGNMENTS

        bool robotBusy = await _db.TaskAssignments.AnyAsync(
            a => a.RobotId == robotId &&
                 a.AssignmentStatus == "ACTIVE",
            cancellationToken);

        if (robotBusy)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    "Robot already has an ACTIVE assignment.");
        }

        var now = DateTime.UtcNow;

        // STEP 9: CREATE MANUAL ASSIGNMENT

        var assignment = new TaskAssignment
        {
            TaskId = task.TaskId,
            RobotId = robot.RobotId,
            AssignmentType = "MANUAL",
            AssignedBy = operatorUserId,
            AssignmentStatus = "ACTIVE",
            BatteryPercentAtAssignment = robot.BatteryPercent,
            WorkloadAtAssignment = 0,
            AssignmentReason =
                $"Manual assignment of {task.TaskTrackingCode} " +
                $"to {robot.RobotCode}.",
            AssignedAt = now
        };

        _db.TaskAssignments.Add(assignment);

        // STEP 10: TASK QUEUED -> ASSIGNED

        string previousTaskStatus = task.TaskStatus;

        task.TaskStatus = TransportTaskStatuses.Assigned;
        task.AssignedAt = now;

        _db.TaskStatusHistories.Add(
            new TaskStatusHistory
            {
                TaskId = task.TaskId,
                PreviousStatus = previousTaskStatus,
                NewStatus = TransportTaskStatuses.Assigned,
                ChangedBy = operatorUserId,
                Reason =
                    $"Assigned manually to robot {robot.RobotCode}.",
                ChangedAt = now
            });

        // STEP 11: ROBOT AVAILABLE -> BUSY

        string previousRobotStatus = robot.OperationalStatus;

        robot.OperationalStatus = "BUSY";
        robot.UpdatedAt = now;

        _db.RobotStatusHistories.Add(
            new RobotStatusHistory
            {
                RobotId = robot.RobotId,
                PreviousOperationalStatus = previousRobotStatus,
                NewOperationalStatus = "BUSY",
                Reason =
                    $"Assigned to task {task.TaskTrackingCode}.",
                ChangedAt = now
            });

        // STEP 12: CREATE MISSION

        var mission = new Mission
        {
            MissionCode = $"TMP-{Guid.NewGuid():N}",
            Assignment = assignment,
            MissionStatus = "INITIALIZING",
            TotalDistanceMeters = 0m,
            TotalDurationSeconds = 0,
            CreatedAt = now
        };

        _db.Missions.Add(mission);

        // Create IDs within the transaction.
        await _db.SaveChangesAsync(cancellationToken);

        mission.MissionCode = $"M{mission.MissionId:000}";

        _db.MissionStatusHistories.Add(
            new MissionStatusHistory
            {
                MissionId = mission.MissionId,
                PreviousStatus = null,
                NewStatus = "INITIALIZING",
                Source = "OPERATOR",
                ChangedBy = operatorUserId,
                Reason =
                    $"Mission created from task {task.TaskTrackingCode}.",
                ChangedAt = now
            });

        // STEP 13: SAVE EXECUTE_TASK COMMAND TO MQTT OUTBOX

        string topic =
            $"smartfleet/robot/{robot.RobotCode}/command";

        string command = JsonSerializer.Serialize(new
        {
            command = "EXECUTE_TASK",
            taskId = task.TaskId,
            assignmentId = assignment.AssignmentId,
            missionId = mission.MissionId,
            pattern = "CIRCLE"
        });

        _db.RobotCommandOutbox.Add(
            new RobotCommandOutbox
            {
                AssignmentId = assignment.AssignmentId,
                Topic = topic,
                Payload = command,
                CreatedAt = now
            });

        // STEP 14: COMMIT

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TransportTaskServiceResult<ManualAssignTaskResponse>
            .Success(new ManualAssignTaskResponse
            {
                TaskId = task.TaskId,
                TaskTrackingCode = task.TaskTrackingCode,
                RobotId = robot.RobotId,
                RobotCode = robot.RobotCode,
                AssignmentId = assignment.AssignmentId,
                MissionId = mission.MissionId,
                MissionCode = mission.MissionCode,
                TaskStatus = task.TaskStatus,
                RobotStatus = robot.OperationalStatus,
                MissionStatus = mission.MissionStatus,
                RobotCommand = command,
                Message =
                    "Task assigned manually and mission created. " +
                    "MQTT command queued for delivery; " +
                    "completion requires robot confirmation."
            });
    }
}
