using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public sealed record RequeueCancelledTaskRequest(string Reason, bool GoodsAtPickupConfirmed);

public sealed class CancelledTaskService(SmartFleetDbContext db, QrDemoRouteService routes,
    IConfiguration configuration, TimeProvider? clock = null)
{
    public async Task<object> Requeue(long taskId, RequeueCancelledTaskRequest request, int actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 400)
            throw new ArgumentException("Enter a reason of 1..400 characters.");
        if (!request.GoodsAtPickupConfirmed)
            throw new ArgumentException("Confirm the goods are available at this task's original pickup point before requeuing.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var task = await GetCancelled(taskId, ct);
        await RequireCompletedCancellation(taskId, ct);
        if (!await TaskLocationPolicy.IsValidAsync(db, taskId, ct))
            throw new InvalidOperationException("The warehouse or pickup/delivery station is inactive or invalid. Restore its configuration before requeuing.");
        if (configuration.GetValue<bool>("DemoRoute:Enabled")) {
            if (!await TaskLocationPolicy.HasVerificationQrAsync(db, taskId, ct))
                throw new InvalidOperationException("Pickup/delivery verification codes must be configured before requeuing.");
            var error = await TaskLocationPolicy.SynchronizeRouteAsync(db, taskId, routes, ct);
            if (error != null) throw new InvalidOperationException(error);
        }
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var control = await db.TaskInterventions.SingleOrDefaultAsync(c => c.TaskId == taskId, ct);
        if (control != null) { control.State = "NORMAL"; control.Paused = false; control.UpdatedAt = now; }
        task.TaskStatus = "QUEUED"; task.QueuedAt = now;
        var earliest = now.AddSeconds(30);
        if (task.ScheduledTime == null || task.ScheduledTime < earliest) task.ScheduledTime = earliest;
        task.AssignedAt = null; task.StartedAt = null; task.CompletedAt = null;
        task.CancelledAt = null; task.CancellationReason = null; task.FailureReason = null;
        db.TaskStatusHistories.Add(new() { TaskId = taskId, PreviousStatus = "CANCELLED", NewStatus = "QUEUED",
            ChangedBy = actor, Reason = request.Reason.Trim(), ChangedAt = now });
        Audit(task, actor, "REQUEUE_CANCELLED_TASK", request.Reason.Trim(), now, true);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { taskId, taskStatus = task.TaskStatus, task.DispatchMode, retryAt = task.ScheduledTime,
            message = "Task requeued. Dispatch mode is preserved. Wait at least 30 seconds before assignment; any later scheduled time still applies." };
    }

    public async Task Delete(long taskId, int actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var task = await GetCancelled(taskId, ct);
        await RequireCompletedCancellation(taskId, ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        task.DeletedAt = now; task.DeletedBy = actor;
        Audit(task, actor, "DELETE_CANCELLED_TASK", "Removed cancelled task from operations; history retained.", now, false);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task<TransportTask> GetCancelled(long taskId, CancellationToken ct)
    {
        var task = await db.TransportTasks.SingleOrDefaultAsync(t => t.TaskId == taskId && t.DeletedAt == null, ct)
            ?? throw new KeyNotFoundException("Task not found or already deleted.");
        if (task.TaskStatus != "CANCELLED") throw new InvalidOperationException("Only a CANCELLED task can use this action. Reload the task list.");
        return task;
    }

    private async Task RequireCompletedCancellation(long taskId, CancellationToken ct)
    {
        if (await db.TaskAssignments.AnyAsync(a => a.TaskId == taskId && a.AssignmentStatus == "ACTIVE", ct) ||
            await db.Missions.AnyAsync(m => m.Assignment.TaskId == taskId && m.MissionStatus != "COMPLETED" &&
                m.MissionStatus != "ABORTED" && m.MissionStatus != "FAILED" && m.MissionStatus != "CANCELLED", ct))
            throw new InvalidOperationException("Cancellation is still in progress. Wait for the robot's safe-stop acknowledgement and mission closure, or ask Maintenance to recover it.");
        var control = await db.TaskInterventions.AsNoTracking().SingleOrDefaultAsync(c => c.TaskId == taskId, ct);
        var unsettledControl = control != null && (control.State is "CANCEL_STOP_PENDING" or "RETURNING_HOME" or "CANCEL_REQUESTED" ||
            !string.IsNullOrEmpty(control.CommandId) && !control.CommandAcknowledged);
        var missingControl = control == null && await db.TaskAssignments.AnyAsync(a => a.TaskId == taskId, ct);
        if (unsettledControl || missingControl)
            throw new InvalidOperationException("The robot has not acknowledged cancellation. Complete Maintenance recovery before requeuing or deleting.");
    }

    private void Audit(TransportTask task, int actor, string action, string reason, DateTime now, bool goods) =>
        db.SystemAuditLogs.Add(new() { UserId = actor, ActionCategory = "TASK_LIFECYCLE", ActionName = action,
            EntityType = "TransportTask", EntityId = task.TaskId.ToString(), CreatedAt = now,
            Description = JsonSerializer.Serialize(new { task.TaskId, task.TaskTrackingCode, userId = actor,
                timestamp = now, reason, goodsAtPickupConfirmed = goods, task.DispatchMode, task.ScheduledTime }) });
}
