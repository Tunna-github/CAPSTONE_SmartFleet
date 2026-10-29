
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

    public TaskDispatchService(
        SmartFleetDbContext db,
        IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    // =====================================================
    // GET ROBOTS AVAILABLE FOR MANUAL ASSIGNMENT
    // =====================================================

    public async Task<
        TransportTaskServiceResult<
            IReadOnlyCollection<AvailableRobotResponse>>>
        GetAvailableRobotsAsync(
            long taskId,
            CancellationToken cancellationToken = default)
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

        var robots = await _db.Robots
            .AsNoTracking()
            .Where(r =>
                r.WarehouseId == task.WarehouseId &&
                r.IsActive &&
                r.RobotType == "PHYSICAL" &&
                r.ConnectionStatus == "ONLINE" &&
                r.OperationalStatus == "AVAILABLE" &&
                r.BatteryPercent != null &&
                r.BatteryPercent >= MinimumBatteryPercent &&
                r.MaxPayloadWeightKg >= weightKg &&

                !_db.TaskAssignments.Any(a =>
                    a.RobotId == r.RobotId &&
                    a.AssignmentStatus == "ACTIVE") &&

                !_db.RobotMaintenanceRecords.Any(m =>
                    m.RobotId == r.RobotId &&
                    (m.MaintenanceStatus == "SCHEDULED" ||
                     m.MaintenanceStatus == "IN_PROGRESS")) &&

                !_db.RobotIncidents.Any(i =>
                    i.RobotId == r.RobotId &&
                    !i.IsResolved &&
                    (i.Severity == "HIGH" ||
                     i.Severity == "CRITICAL")))
            .OrderByDescending(r => r.BatteryPercent)
            .ThenBy(r => r.RobotId)
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
    // ASSIGN TASK MANUALLY
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
