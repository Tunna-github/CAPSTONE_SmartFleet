
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartFleetBE.Constants;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

// Select the best eligible robot when a new task is created.
// Assignment, Mission and MQTT Outbox are saved atomically.
public sealed class AutoDispatchService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AutoDispatchService> logger)
{
    private const decimal MinimumBatteryPercent = 20m;
    private const int MaxAttempts = 3;

    public async Task<bool> TryAssignAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        // DB payload limits may contain placeholder values.
        // Require an additional verified physical safety limit.
        var verifiedPayloadLimitKg =
            configuration.GetValue<decimal>(
                "AutoDispatch:VerifiedPayloadLimitKg");

        if (verifiedPayloadLimitKg <= 0m)
        {
            logger.LogWarning(
                "AUTO DISPATCH skipped Task {TaskId}: " +
                "AutoDispatch:VerifiedPayloadLimitKg " +
                "is not configured.",
                taskId);

            return false;
        }

        // Retry conflicts if multiple Tasks try to
        // reserve the same robot simultaneously.
        for (var attempt = 1;
             attempt <= MaxAttempts;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Each attempt uses a fresh DbContext.
                using var scope = scopeFactory.CreateScope();

                var db = scope.ServiceProvider
                    .GetRequiredService<SmartFleetDbContext>();

                await using var transaction =
                    await db.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                // ======================================
                // STEP 1: LOAD TASK
                // ======================================

                var task = await db.TransportTasks
                    .SingleOrDefaultAsync(
                        t => t.TaskId == taskId,
                        cancellationToken);

                if (task is null ||
                    task.TaskStatus !=
                        TransportTaskStatuses.Queued)
                {
                    logger.LogInformation(
                        "AUTO DISPATCH skipped Task {TaskId}: " +
                        "task does not exist or is not QUEUED.",
                        taskId);

                    return false;
                }

                if (task.PayloadWeightKg is not > 0m)
                {
                    logger.LogInformation(
                        "AUTO DISPATCH skipped Task {TaskId}: " +
                        "payload weight must be greater than zero.",
                        taskId);

                    return false;
                }

                var now = DateTime.UtcNow;
                var weightKg = task.PayloadWeightKg.Value;

                // Do not dispatch future scheduled tasks.
                if (task.ScheduledTime is { } scheduled &&
                    scheduled > now)
                {
                    logger.LogInformation(
                        "AUTO DISPATCH skipped Task {TaskId}: " +
                        "scheduled time is in the future.",
                        taskId);

                    return false;
                }

                // Check verified physical payload limit.
                if (weightKg > verifiedPayloadLimitKg)
                {
                    logger.LogInformation(
                        "AUTO DISPATCH skipped Task {TaskId}: " +
                        "weight {Weight} kg exceeds " +
                        "verified limit {Limit} kg.",
                        taskId,
                        weightKg,
                        verifiedPayloadLimitKg);

                    return false;
                }

                // ======================================
                // STEP 2: PREVENT DUPLICATE ASSIGNMENT
                // ======================================

                var taskHasActiveAssignment =
                    await db.TaskAssignments.AnyAsync(
                        a => a.TaskId == taskId &&
                             a.AssignmentStatus == "ACTIVE",
                        cancellationToken);

                if (taskHasActiveAssignment)
                {
                    logger.LogInformation(
                        "AUTO DISPATCH skipped Task {TaskId}: " +
                        "Task already has an ACTIVE assignment.",
                        taskId);

                    return false;
                }

                // ======================================
                // STEP 3: FIND THE BEST ELIGIBLE ROBOT
                // ======================================

                var robot = await db.Robots
                    .Where(r =>
                        // Same warehouse
                        r.WarehouseId == task.WarehouseId &&

                        // Physical and enabled robot
                        r.RobotType == "PHYSICAL" &&
                        r.IsActive &&

                        // Robot must be online and free
                        r.ConnectionStatus == "ONLINE" &&
                        r.OperationalStatus == "AVAILABLE" &&

                        // Minimum battery
                        r.BatteryPercent != null &&
                        r.BatteryPercent >=
                            MinimumBatteryPercent &&

                        // Robot-specific payload capacity
                        r.MaxPayloadWeightKg >= weightKg &&

                        // Robot must not have active tasks
                        !db.TaskAssignments.Any(a =>
                            a.RobotId == r.RobotId &&
                            a.AssignmentStatus == "ACTIVE") &&

                        // Robot must not be under maintenance
                        !db.RobotMaintenanceRecords.Any(m =>
                            m.RobotId == r.RobotId &&
                            (
                                m.MaintenanceStatus ==
                                    "SCHEDULED" ||
                                m.MaintenanceStatus ==
                                    "IN_PROGRESS"
                            )) &&

                        // No unresolved severe incidents
                        !db.RobotIncidents.Any(i =>
                            i.RobotId == r.RobotId &&
                            !i.IsResolved &&
                            (
                                i.Severity == "HIGH" ||
                                i.Severity == "CRITICAL"
                            )))
                    .OrderByDescending(r => r.BatteryPercent)
                    .ThenBy(r => r.RobotId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (robot is null)
                {
                    logger.LogInformation(
                        "AUTO DISPATCH: no eligible robot " +
                        "for Task {TaskId}. " +
                        "Task remains QUEUED.",
                        taskId);

                    return false;
                }

                // ======================================
                // STEP 4: CREATE AUTO ASSIGNMENT
                // ======================================

                var assignment = new TaskAssignment
                {
                    TaskId = task.TaskId,
                    RobotId = robot.RobotId,

                    AssignmentType = "AUTO",
                    AssignedBy = null,

                    AssignmentStatus = "ACTIVE",

                    BatteryPercentAtAssignment =
                        robot.BatteryPercent,

                    WorkloadAtAssignment = 0,

                    AssignmentReason =
                        $"Automatically assigned " +
                        $"{task.TaskTrackingCode} to " +
                        $"{robot.RobotCode} " +
                        $"(highest eligible battery).",

                    AssignedAt = now
                };

                db.TaskAssignments.Add(assignment);

                // ======================================
                // STEP 5: TASK QUEUED -> ASSIGNED
                // ======================================

                var previousTaskStatus = task.TaskStatus;

                task.TaskStatus =
                    TransportTaskStatuses.Assigned;

                task.AssignedAt = now;

                db.TaskStatusHistories.Add(
                    new TaskStatusHistory
                    {
                        TaskId = task.TaskId,

                        PreviousStatus = previousTaskStatus,

                        NewStatus =
                            TransportTaskStatuses.Assigned,

                        ChangedBy = null,

                        Reason =
                            $"AUTO assignment to " +
                            $"{robot.RobotCode}.",

                        ChangedAt = now
                    });

                // ======================================
                // STEP 6: ROBOT AVAILABLE -> BUSY
                // ======================================

                var previousRobotStatus =
                    robot.OperationalStatus;

                robot.OperationalStatus = "BUSY";
                robot.UpdatedAt = now;

                db.RobotStatusHistories.Add(
                    new RobotStatusHistory
                    {
                        RobotId = robot.RobotId,

                        PreviousOperationalStatus =
                            previousRobotStatus,

                        NewOperationalStatus = "BUSY",

                        Reason =
                            $"AUTO assignment to " +
                            $"{task.TaskTrackingCode}.",

                        ChangedAt = now
                    });

                // ======================================
                // STEP 7: CREATE MISSION
                // ======================================

                var mission = new Mission
                {
                    MissionCode =
                        $"TMP-{Guid.NewGuid():N}",

                    Assignment = assignment,

                    MissionStatus = "INITIALIZING",

                    TotalDistanceMeters = 0m,
                    TotalDurationSeconds = 0,

                    CreatedAt = now
                };

                db.Missions.Add(mission);

                // Obtain generated IDs inside transaction.
                await db.SaveChangesAsync(
                    cancellationToken);

                // Same mission-code convention as main.
                mission.MissionCode =
                    $"M{mission.MissionId:000}";

                db.MissionStatusHistories.Add(
                    new MissionStatusHistory
                    {
                        MissionId = mission.MissionId,

                        PreviousStatus = null,
                        NewStatus = "INITIALIZING",

                        Source = "SYSTEM",
                        ChangedBy = null,

                        Reason =
                            $"Created by AUTO dispatch of " +
                            $"{task.TaskTrackingCode}.",

                        ChangedAt = now
                    });

                // ======================================
                // STEP 8: CREATE MQTT OUTBOX COMMAND
                // ======================================

                // The existing Outbox Worker publishes
                // this command AFTER transaction commit.

                var topic =
                    $"smartfleet/robot/" +
                    $"{robot.RobotCode}/command";

                var payload = JsonSerializer.Serialize(
                    new
                    {
                        command = "EXECUTE_TASK",

                        taskId = task.TaskId,

                        assignmentId =
                            assignment.AssignmentId,

                        missionId = mission.MissionId,

                        // Existing JetRacer test movement
                        pattern = "CIRCLE"
                    });

                db.RobotCommandOutbox.Add(
                    new RobotCommandOutbox
                    {
                        AssignmentId =
                            assignment.AssignmentId,

                        Topic = topic,
                        Payload = payload,

                        CreatedAt = now
                    });

                // ======================================
                // STEP 9: SAVE AND COMMIT
                // ======================================

                await db.SaveChangesAsync(
                    cancellationToken);

                await transaction.CommitAsync(
                    cancellationToken);

                logger.LogInformation(
                    "AUTO DISPATCH SUCCESS: " +
                    "TaskId={TaskId}, " +
                    "Robot={RobotCode}, " +
                    "Battery={Battery}%, " +
                    "AssignmentId={AssignmentId}, " +
                    "MissionId={MissionId}",
                    task.TaskId,
                    robot.RobotCode,
                    robot.BatteryPercent,
                    assignment.AssignmentId,
                    mission.MissionId);

                return true;
            }
            catch (Exception ex) when (
                IsRetryablePostgresConflict(ex) &&
                attempt < MaxAttempts)
            {
                logger.LogWarning(
                    ex,
                    "AUTO DISPATCH conflict for Task {TaskId}; " +
                    "retrying attempt {NextAttempt}/{MaxAttempts}.",
                    taskId,
                    attempt + 1,
                    MaxAttempts);
            }
        }

        return false;
    }

    private static bool IsRetryablePostgresConflict(
        Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException pg &&
                (
                    pg.SqlState == "40001" ||
                    pg.SqlState == "40P01" ||
                    pg.SqlState == "23505"
                ))
            {
                return true;
            }
        }

        return false;
    }
}