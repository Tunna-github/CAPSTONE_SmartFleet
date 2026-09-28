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
    private readonly MqttService _mqttService;

    public TaskDispatchService(
        SmartFleetDbContext dbContext,
        MqttService mqttService)
    {
        _dbContext = dbContext;
        _mqttService = mqttService;
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
    // MANUAL ASSIGNMENT + CREATE MISSION + TEST MOVE
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
                .BeginTransactionAsync(cancellationToken);


        // -------------------------------------------------
        // 1. FIND TASK
        // -------------------------------------------------

        var task = await _dbContext.TransportTasks
            .FirstOrDefaultAsync(
                t => t.TaskId == taskId,
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
        // 3. FIND ROBOT
        // -------------------------------------------------

        var robot = await _dbContext.Robots
            .FirstOrDefaultAsync(
                r => r.RobotId == robotId,
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
        // 5. CONFIRM ASSIGNMENT
        // -------------------------------------------------

        var now = DateTime.UtcNow;


        var assignment = new TaskAssignment
        {
            TaskId = task.TaskId,

            RobotId = robot.RobotId,

            AssignmentType = "MANUAL",

            AssignedBy = operatorUserId,

            AssignmentStatus = "ACTIVE",

            BatteryPercentAtAssignment =
                robot.BatteryPercent,

            WorkloadAtAssignment = 0,

            AssignmentReason =
                $"Manual assignment of {task.TaskTrackingCode} to {robot.RobotCode}.",

            AssignedAt = now
        };


        _dbContext.TaskAssignments.Add(
            assignment);


        // -------------------------------------------------
        // 6. UPDATE TASK
        // QUEUED -> ASSIGNED
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
                TaskId = task.TaskId,

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
        // 7. UPDATE ROBOT
        // AVAILABLE -> BUSY
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
                // Temporary unique code.
                // Changed to M001/M002/... after identity is created.
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


        // First save:
        // generates AssignmentID + MissionID
        await _dbContext.SaveChangesAsync(
            cancellationToken);


        // Example:
        // MissionID = 1 -> M001
        // MissionID = 2 -> M002
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


        await _dbContext.SaveChangesAsync(
            cancellationToken);


        // -------------------------------------------------
        // 9. SEND SAME TEST COMMAND AS EXISTING API
        // -------------------------------------------------

        var topic =
            $"smartfleet/robot/{robot.RobotCode}/command";

        const string command =
            "TEST_MOVE";


        try
        {
            await _mqttService.PublishAsync(
                topic,
                command,
                cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(
                cancellationToken);

            throw;
        }


        // -------------------------------------------------
        // 10. COMPLETE
        // -------------------------------------------------

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
                        "Task assigned, mission created, and test move command sent to robot."
                });
    }
}