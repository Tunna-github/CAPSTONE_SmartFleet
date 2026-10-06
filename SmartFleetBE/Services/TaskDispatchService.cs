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

    private readonly SmartFleetDbContext _dbContext;

    public TaskDispatchService(
        SmartFleetDbContext dbContext)
    {
        _dbContext = dbContext;
    }


    // =====================================================
    // VIEW AVAILABLE ROBOTS
    // =====================================================

    public async Task<
        TransportTaskServiceResult<
            IReadOnlyCollection<AvailableRobotResponse>>>
        GetAvailableRobotsAsync(
            long taskId,
            CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.TransportTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.TaskId == taskId,
                cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<
                IReadOnlyCollection<AvailableRobotResponse>>
                .NotFound(
                    $"Transport task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Queued,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                IReadOnlyCollection<AvailableRobotResponse>>
                .Conflict(
                    $"Task must be QUEUED before assignment. Current status: {task.TaskStatus}.");
        }


        var robots = await _dbContext.Robots
            .AsNoTracking()
            .Where(r =>
                r.WarehouseId == task.WarehouseId
                &&
                r.IsActive
                &&
                r.ConnectionStatus == "ONLINE"
                &&
                r.OperationalStatus == "AVAILABLE"
                &&
                r.BatteryPercent != null
                &&
                r.BatteryPercent >= MinimumBatteryPercent
                &&
                !_dbContext.TaskAssignments.Any(a =>
                    a.RobotId == r.RobotId &&
                    a.AssignmentStatus == "ACTIVE")
                &&
                !_dbContext.RobotMaintenanceRecords.Any(m =>
                    m.RobotId == r.RobotId &&
                    (
                        m.MaintenanceStatus == "SCHEDULED" ||
                        m.MaintenanceStatus == "IN_PROGRESS"
                    )))
            .OrderByDescending(r => r.BatteryPercent)
            .Select(r => new AvailableRobotResponse
            {
                RobotId = r.RobotId,
                RobotCode = r.RobotCode,
                BatteryPercent = r.BatteryPercent,
                ConnectionStatus = r.ConnectionStatus,
                OperationalStatus = r.OperationalStatus
            })
            .ToListAsync(cancellationToken);


        return TransportTaskServiceResult<
            IReadOnlyCollection<AvailableRobotResponse>>
            .Success(robots);
    }


    // =====================================================
    // MANUAL ASSIGNMENT
    // =====================================================

    public async Task<
        TransportTaskServiceResult<ManualAssignTaskResponse>>
        AssignManualAsync(
            long taskId,
            int robotId,
            int operatorUserId,
            CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);


        // -------------------------------------------------
        // 1. LOCK TASK FIRST
        // -------------------------------------------------

        var taskQuery = _dbContext.Database.IsSqlServer()
            ? _dbContext.TransportTasks.FromSqlInterpolated(
                $"SELECT * FROM dbo.TransportTasks WITH (UPDLOCK, HOLDLOCK) WHERE TaskID = {taskId}")
            : _dbContext.TransportTasks.Where(
                t => t.TaskId == taskId);

        var task =
            await taskQuery.SingleOrDefaultAsync(
                cancellationToken);


        if (task is null)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .NotFound(
                    $"Transport task {taskId} was not found.");
        }


        // -------------------------------------------------
        // 2. VALIDATE TASK
        // -------------------------------------------------

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Queued,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    $"Task must be QUEUED. Current status: {task.TaskStatus}.");
        }


        var taskAlreadyAssigned =
            await _dbContext.TaskAssignments
                .AnyAsync(
                    a =>
                        a.TaskId == taskId &&
                        a.AssignmentStatus == "ACTIVE",
                    cancellationToken);


        if (taskAlreadyAssigned)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Task already has an active assignment.");
        }


        // -------------------------------------------------
        // 3. LOCK ROBOT
        // -------------------------------------------------

        var robotQuery = _dbContext.Database.IsSqlServer()
            ? _dbContext.Robots.FromSqlInterpolated(
                $"SELECT * FROM dbo.Robots WITH (UPDLOCK, HOLDLOCK) WHERE RobotID = {robotId}")
            : _dbContext.Robots.Where(
                r => r.RobotId == robotId);


        var robot =
            await robotQuery.SingleOrDefaultAsync(
                cancellationToken);


        if (robot is null)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .NotFound(
                    $"Robot {robotId} was not found.");
        }


        // -------------------------------------------------
        // 4. VALIDATE ROBOT
        // -------------------------------------------------

        if (!robot.IsActive)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Robot is inactive.");
        }


        if (robot.WarehouseId != task.WarehouseId)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Robot and task must belong to the same warehouse.");
        }


        if (!string.Equals(
                robot.ConnectionStatus,
                "ONLINE",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Robot is offline.");
        }


        if (!string.Equals(
                robot.OperationalStatus,
                "AVAILABLE",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    $"Robot is not available. Current status: {robot.OperationalStatus}.");
        }


        if (!robot.BatteryPercent.HasValue)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Robot battery information is unavailable.");
        }


        if (robot.BatteryPercent.Value <
            MinimumBatteryPercent)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    $"Robot battery must be at least {MinimumBatteryPercent}%.");
        }


        var hasActiveMaintenance =
            await _dbContext.RobotMaintenanceRecords
                .AnyAsync(
                    m =>
                        m.RobotId == robotId &&
                        (
                            m.MaintenanceStatus == "SCHEDULED" ||
                            m.MaintenanceStatus == "IN_PROGRESS"
                        ),
                    cancellationToken);


        if (hasActiveMaintenance)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Robot is under maintenance.");
        }


        var robotAlreadyBusy =
            await _dbContext.TaskAssignments
                .AnyAsync(
                    a =>
                        a.RobotId == robotId &&
                        a.AssignmentStatus == "ACTIVE",
                    cancellationToken);


        if (robotAlreadyBusy)
        {
            return TransportTaskServiceResult<
                ManualAssignTaskResponse>
                .Conflict(
                    "Robot already has an active task.");
        }


        // -------------------------------------------------
        // 5. CREATE ASSIGNMENT
        // -------------------------------------------------

        var now = DateTime.UtcNow;


        var assignment =
            new TaskAssignment
            {
                TaskId =
                    task.TaskId,

                RobotId =
                    robot.RobotId,

                AssignmentType =
                    "MANUAL",

                AssignedBy =
                    operatorUserId,

                AssignmentStatus =
                    "ACTIVE",

                BatteryPercentAtAssignment =
                    robot.BatteryPercent,

                WorkloadAtAssignment =
                    0,

                AssignmentReason =
                    $"Manual assignment of {task.TaskTrackingCode} to {robot.RobotCode}.",

                AssignedAt =
                    now
            };


        _dbContext.TaskAssignments.Add(
            assignment);


        // -------------------------------------------------
        // 6. TASK -> ASSIGNED
        // -------------------------------------------------

        var previousTaskStatus =
            task.TaskStatus;


        task.TaskStatus =
            TransportTaskStatuses.Assigned;

        task.AssignedAt =
            now;


        _dbContext.TaskStatusHistories.Add(
            new TaskStatusHistory
            {
                TaskId =
                    task.TaskId,

                PreviousStatus =
                    previousTaskStatus,

                NewStatus =
                    TransportTaskStatuses.Assigned,

                ChangedBy =
                    operatorUserId,

                Reason =
                    $"Assigned manually to robot {robot.RobotCode}.",

                ChangedAt =
                    now
            });


        // -------------------------------------------------
        // 7. ROBOT -> BUSY
        // -------------------------------------------------

        var previousRobotStatus =
            robot.OperationalStatus;


        robot.OperationalStatus =
            "BUSY";

        robot.UpdatedAt =
            now;


        _dbContext.RobotStatusHistories.Add(
            new RobotStatusHistory
            {
                RobotId =
                    robot.RobotId,

                PreviousOperationalStatus =
                    previousRobotStatus,

                NewOperationalStatus =
                    "BUSY",

                Reason =
                    $"Assigned to task {task.TaskTrackingCode}.",

                ChangedAt =
                    now
            });


        // -------------------------------------------------
        // 8. CREATE MISSION
        // -------------------------------------------------

        var mission =
            new Mission
            {
                MissionCode =
                    $"TMP-{Guid.NewGuid():N}",

                Assignment =
                    assignment,

                MissionStatus =
                    "INITIALIZING",

                TotalDistanceMeters =
                    0,

                TotalDurationSeconds =
                    0,

                CreatedAt =
                    now
            };


        _dbContext.Missions.Add(
            mission);


        // Generates AssignmentID and MissionID.
        await _dbContext.SaveChangesAsync(
            cancellationToken);


        mission.MissionCode =
            $"M{mission.MissionId:000}";


        _dbContext.MissionStatusHistories.Add(
            new MissionStatusHistory
            {
                MissionId =
                    mission.MissionId,

                PreviousStatus =
                    null,

                NewStatus =
                    "INITIALIZING",

                Source =
                    "OPERATOR",

                ChangedBy =
                    operatorUserId,

                Reason =
                    $"Mission created from transport task {task.TaskTrackingCode}.",

                ChangedAt =
                    now
            });


        // -------------------------------------------------
        // 9. CREATE MQTT OUTBOX COMMAND
        // -------------------------------------------------

        var topic =
            $"smartfleet/robot/{robot.RobotCode}/command";


        var command =
            JsonSerializer.Serialize(
                new
                {
                    command =
                        "EXECUTE_TASK",

                    taskId =
                        task.TaskId,

                    assignmentId =
                        assignment.AssignmentId,

                    missionId =
                        mission.MissionId,

                    pattern =
                        "CIRCLE"
                });


        _dbContext.RobotCommandOutbox.Add(
            new RobotCommandOutbox
            {
                AssignmentId =
                    assignment.AssignmentId,

                Topic =
                    topic,

                Payload =
                    command,

                CreatedAt =
                    now
            });


        await _dbContext.SaveChangesAsync(
            cancellationToken);


        await transaction.CommitAsync(
            cancellationToken);


        return TransportTaskServiceResult<
            ManualAssignTaskResponse>
            .Success(
                new ManualAssignTaskResponse
                {
                    TaskId =
                        task.TaskId,

                    TaskTrackingCode =
                        task.TaskTrackingCode,

                    RobotId =
                        robot.RobotId,

                    RobotCode =
                        robot.RobotCode,

                    AssignmentId =
                        assignment.AssignmentId,

                    MissionId =
                        mission.MissionId,

                    MissionCode =
                        mission.MissionCode,

                    TaskStatus =
                        task.TaskStatus,

                    RobotStatus =
                        robot.OperationalStatus,

                    MissionStatus =
                        mission.MissionStatus,

                    RobotCommand =
                        command,

                    Message =
                        "Task assigned and mission created. Robot command queued for delivery; completion requires robot confirmation."
                });
    }


    // =====================================================
    // CANCEL TRANSPORT TASK
    // =====================================================

    public async Task<
        TransportTaskServiceResult<CancelTransportTaskResponse>>
        CancelAsync(
            long taskId,
            string? reason,
            int operatorUserId,
            CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);


        // -------------------------------------------------
        // 1. LOCK TASK
        // -------------------------------------------------

        var taskQuery = _dbContext.Database.IsSqlServer()
            ? _dbContext.TransportTasks.FromSqlInterpolated(
                $"SELECT * FROM dbo.TransportTasks WITH (UPDLOCK, HOLDLOCK) WHERE TaskID = {taskId}")
            : _dbContext.TransportTasks.Where(
                t => t.TaskId == taskId);


        var task =
            await taskQuery.SingleOrDefaultAsync(
                cancellationToken);


        if (task is null)
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .NotFound(
                    $"Transport task {taskId} was not found.");
        }


        // -------------------------------------------------
        // 2. VALIDATE TASK STATUS
        // -------------------------------------------------

        if (string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Cancelled,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .Conflict(
                    "Task is already CANCELLED.");
        }


        if (string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Completed,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .Conflict(
                    "A COMPLETED task cannot be cancelled.");
        }


        if (string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Failed,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .Conflict(
                    "A FAILED task cannot be cancelled.");
        }


        // For now we only release tasks before robot execution.
        // EXECUTING cancellation needs a physical STOP/ABORT command
        // supported by the Robot Agent.
        if (string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Executing,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .Conflict(
                    "An EXECUTING task cannot be cancelled safely yet because the Robot Agent does not have a confirmed STOP/ABORT command.");
        }


        var previousTaskStatus =
            task.TaskStatus;


        var normalizedReason =
            string.IsNullOrWhiteSpace(reason)
                ? "Cancelled by Warehouse Operator."
                : reason.Trim();


        if (normalizedReason.Length > 500)
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .ValidationFailed(
                    "Cancellation reason cannot exceed 500 characters.");
        }


        var now =
            DateTime.UtcNow;


        // -------------------------------------------------
        // 3. FIND ACTIVE ASSIGNMENT
        // -------------------------------------------------

        var assignmentQuery = _dbContext.Database.IsSqlServer()
            ? _dbContext.TaskAssignments.FromSqlInterpolated(
                $"SELECT * FROM dbo.TaskAssignments WITH (UPDLOCK, HOLDLOCK) WHERE TaskID = {taskId} AND AssignmentStatus = 'ACTIVE'")
            : _dbContext.TaskAssignments.Where(
                a =>
                    a.TaskId == taskId &&
                    a.AssignmentStatus == "ACTIVE");


        var assignment =
            await assignmentQuery.SingleOrDefaultAsync(
                cancellationToken);


        if (string.Equals(
                previousTaskStatus,
                TransportTaskStatuses.Assigned,
                StringComparison.OrdinalIgnoreCase)
            &&
            assignment is null)
        {
            return TransportTaskServiceResult<
                CancelTransportTaskResponse>
                .Conflict(
                    "Task is ASSIGNED but no ACTIVE assignment was found. Database state is inconsistent.");
        }


        Robot? robot = null;
        Mission? mission = null;


        // -------------------------------------------------
        // 4. CANCEL ASSIGNMENT + ABORT MISSION
        // -------------------------------------------------

        if (assignment is not null)
        {
            var robotQuery = _dbContext.Database.IsSqlServer()
                ? _dbContext.Robots.FromSqlInterpolated(
                    $"SELECT * FROM dbo.Robots WITH (UPDLOCK, HOLDLOCK) WHERE RobotID = {assignment.RobotId}")
                : _dbContext.Robots.Where(
                    r => r.RobotId == assignment.RobotId);


            robot =
                await robotQuery.SingleOrDefaultAsync(
                    cancellationToken);


            mission =
                await _dbContext.Missions
                    .SingleOrDefaultAsync(
                        m =>
                            m.AssignmentId ==
                            assignment.AssignmentId,
                        cancellationToken);


            // ---------------------------------------------
            // ABORT MISSION
            // ---------------------------------------------

            if (mission is not null &&
                mission.MissionStatus is not
                    ("COMPLETED" or "FAILED" or "ABORTED"))
            {
                var previousMissionStatus =
                    mission.MissionStatus;


                mission.MissionStatus =
                    "ABORTED";

                mission.EndExecutionTime =
                    now;

                mission.LastProgressAt =
                    now;


                _dbContext.MissionStatusHistories.Add(
                    new MissionStatusHistory
                    {
                        MissionId =
                            mission.MissionId,

                        PreviousStatus =
                            previousMissionStatus,

                        NewStatus =
                            "ABORTED",

                        Source =
                            "OPERATOR",

                        ChangedBy =
                            operatorUserId,

                        Reason =
                            normalizedReason,

                        ChangedAt =
                            now
                    });
            }


            // ---------------------------------------------
            // CANCEL ASSIGNMENT
            // ---------------------------------------------

            assignment.AssignmentStatus =
                "CANCELLED";

            assignment.EndedAt =
                now;

            assignment.ReassignmentReason =
                normalizedReason;


            // ---------------------------------------------
            // PREVENT UNSENT EXECUTE COMMAND
            // ---------------------------------------------

            var pendingCommand =
                await _dbContext.RobotCommandOutbox
                    .FirstOrDefaultAsync(
                        c =>
                            c.AssignmentId ==
                                assignment.AssignmentId
                            &&
                            c.DeliveredAt == null,
                        cancellationToken);


            if (pendingCommand is not null)
            {
                pendingCommand.DeliveredAt =
                    now;
            }


            // ---------------------------------------------
            // RELEASE ROBOT
            // ---------------------------------------------

            if (robot is not null)
            {
                var anotherActiveAssignment =
                    await _dbContext.TaskAssignments
                        .AnyAsync(
                            a =>
                                a.RobotId ==
                                    robot.RobotId
                                &&
                                a.AssignmentId !=
                                    assignment.AssignmentId
                                &&
                                a.AssignmentStatus ==
                                    "ACTIVE",
                            cancellationToken);


                var activeMaintenance =
                    await _dbContext.RobotMaintenanceRecords
                        .AnyAsync(
                            m =>
                                m.RobotId ==
                                    robot.RobotId
                                &&
                                (
                                    m.MaintenanceStatus ==
                                        "SCHEDULED"
                                    ||
                                    m.MaintenanceStatus ==
                                        "IN_PROGRESS"
                                ),
                            cancellationToken);


                if (!anotherActiveAssignment &&
                    (
                        robot.OperationalStatus == "BUSY"
                        ||
                        robot.OperationalStatus == "ASSIGNED"
                    ))
                {
                    var previousRobotStatus =
                        robot.OperationalStatus;


                    var nextRobotStatus =
                        activeMaintenance
                            ? "MAINTENANCE"
                            : "AVAILABLE";


                    robot.OperationalStatus =
                        nextRobotStatus;

                    robot.UpdatedAt =
                        now;


                    _dbContext.RobotStatusHistories.Add(
                        new RobotStatusHistory
                        {
                            RobotId =
                                robot.RobotId,

                            PreviousOperationalStatus =
                                previousRobotStatus,

                            NewOperationalStatus =
                                nextRobotStatus,

                            Reason =
                                normalizedReason,

                            ChangedAt =
                                now
                        });
                }
            }
        }


        // -------------------------------------------------
        // 5. TASK -> CANCELLED
        // -------------------------------------------------

        task.TaskStatus =
            TransportTaskStatuses.Cancelled;

        task.CancelledAt =
            now;

        task.CancellationReason =
            normalizedReason;


        _dbContext.TaskStatusHistories.Add(
            new TaskStatusHistory
            {
                TaskId =
                    task.TaskId,

                PreviousStatus =
                    previousTaskStatus,

                NewStatus =
                    TransportTaskStatuses.Cancelled,

                ChangedBy =
                    operatorUserId,

                Reason =
                    normalizedReason,

                ChangedAt =
                    now
            });


        // -------------------------------------------------
        // 6. SAVE TRANSACTION
        // -------------------------------------------------

        await _dbContext.SaveChangesAsync(
            cancellationToken);


        await transaction.CommitAsync(
            cancellationToken);


        // -------------------------------------------------
        // 7. RESPONSE
        // -------------------------------------------------

        return TransportTaskServiceResult<
            CancelTransportTaskResponse>
            .Success(
                new CancelTransportTaskResponse
                {
                    TaskId =
                        task.TaskId,

                    TaskTrackingCode =
                        task.TaskTrackingCode,

                    PreviousTaskStatus =
                        previousTaskStatus,

                    TaskStatus =
                        task.TaskStatus,

                    AssignmentId =
                        assignment?.AssignmentId,

                    AssignmentStatus =
                        assignment?.AssignmentStatus,

                    RobotId =
                        robot?.RobotId,

                    RobotCode =
                        robot?.RobotCode,

                    RobotStatus =
                        robot?.OperationalStatus,

                    MissionId =
                        mission?.MissionId,

                    MissionCode =
                        mission?.MissionCode,

                    MissionStatus =
                        mission?.MissionStatus,

                    CancelledAt =
                        now,

                    CancellationReason =
                        normalizedReason,

                    Message =
                        "Task cancelled successfully. Active assignment was closed, mission was aborted, and robot was released when safe."
                });
    }
}