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
    private readonly SmartFleetDbContext _dbContext;
    private readonly MqttService _mqttService;

    public TaskDispatchService(
        SmartFleetDbContext dbContext,
        MqttService mqttService)
    {
        _dbContext = dbContext;
        _mqttService = mqttService;
    }

    public async Task<TransportTaskServiceResult<ManualAssignTaskResponse>>
        AssignManualAsync(
            long taskId,
            int robotId,
            string movementPattern,
            int operatorUserId,
            CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.TransportTasks
            .FirstOrDefaultAsync(
                t => t.TaskId == taskId,
                cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .NotFound($"Transport task {taskId} was not found.");
        }

        // For this test, allow assignment only from PENDING/QUEUED.
        if (task.TaskStatus != TransportTaskStatuses.Pending &&
            task.TaskStatus != TransportTaskStatuses.Queued)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    $"Task cannot be assigned because its current status is {task.TaskStatus}.");
        }

        var robot = await _dbContext.Robots
            .FirstOrDefaultAsync(
                r => r.RobotId == robotId,
                cancellationToken);

        if (robot is null)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .NotFound($"Robot {robotId} was not found.");
        }

        if (!robot.IsActive)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot is inactive.");
        }

        if (robot.WarehouseId != task.WarehouseId)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    "Robot and transport task must belong to the same warehouse.");
        }

        if (!string.Equals(
                robot.ConnectionStatus,
                "ONLINE",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot is currently offline.");
        }

        if (!string.Equals(
                robot.OperationalStatus,
                "AVAILABLE",
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict(
                    $"Robot is not available. Current status: {robot.OperationalStatus}.");
        }

        var robotHasActiveTask = await _dbContext.TaskAssignments
            .AnyAsync(
                a => a.RobotId == robotId &&
                     a.AssignmentStatus == "ACTIVE",
                cancellationToken);

        if (robotHasActiveTask)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot already has an active task.");
        }

        var taskAlreadyAssigned = await _dbContext.TaskAssignments
            .AnyAsync(
                a => a.TaskId == taskId &&
                     a.AssignmentStatus == "ACTIVE",
                cancellationToken);

        if (taskAlreadyAssigned)
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Task already has an active assignment.");
        }

        movementPattern = movementPattern.Trim().ToUpperInvariant();

        if (movementPattern != "FIGURE_8" &&
            movementPattern != "CIRCLE")
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .ValidationFailed(
                    "MovementPattern must be FIGURE_8 or CIRCLE.");
        }

        var now = DateTime.UtcNow;

        var assignment = new TaskAssignment
        {
            TaskId = task.TaskId,
            RobotId = robot.RobotId,

            AssignmentType = "MANUAL",
            AssignedBy = operatorUserId,
            AssignmentStatus = "ACTIVE",

            BatteryPercentAtAssignment = robot.BatteryPercent,

            AssignmentReason =
                $"Manual assignment for {movementPattern} integration test.",

            AssignedAt = now
        };

        _dbContext.TaskAssignments.Add(assignment);

        var previousStatus = task.TaskStatus;

        // User requirement:
        // Robot accepts task -> task becomes EXECUTING.
        task.TaskStatus = TransportTaskStatuses.Executing;
        task.AssignedAt = now;
        task.StartedAt = now;

        robot.OperationalStatus = "BUSY";
        robot.UpdatedAt = now;

        _dbContext.TaskStatusHistories.Add(
            new TaskStatusHistory
            {
                TaskId = task.TaskId,
                PreviousStatus = previousStatus,
                NewStatus = TransportTaskStatuses.Executing,
                ChangedBy = operatorUserId,
                Reason =
                    $"Manually assigned to robot {robot.RobotCode}.",
                ChangedAt = now
            });

        await _dbContext.SaveChangesAsync(cancellationToken);

        // MQTT command
        var topic =
            $"smartfleet/robot/{robot.RobotCode}/command";

        var command = JsonSerializer.Serialize(new
        {
            command = "EXECUTE_TASK",
            taskId = task.TaskId,
            assignmentId = assignment.AssignmentId,
            pattern = movementPattern
        });

        try
        {
            await _mqttService.PublishAsync(
                topic,
                command,
                cancellationToken);
        }
        catch
        {
            // MQTT failed -> rollback operational state.
            assignment.AssignmentStatus = "FAILED";
            assignment.EndedAt = DateTime.UtcNow;

            task.TaskStatus = previousStatus;
            task.AssignedAt = null;
            task.StartedAt = null;

            robot.OperationalStatus = "AVAILABLE";
            robot.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            throw;
        }

        return TransportTaskServiceResult<ManualAssignTaskResponse>.Success(
            new ManualAssignTaskResponse
            {
                TaskId = task.TaskId,
                TaskTrackingCode = task.TaskTrackingCode,

                RobotId = robot.RobotId,
                RobotCode = robot.RobotCode,

                AssignmentId = assignment.AssignmentId,

                TaskStatus = task.TaskStatus,
                RobotStatus = robot.OperationalStatus,

                MovementPattern = movementPattern,

                Message =
                    "Robot accepted the task. Task is now executing."
            });
    }
}