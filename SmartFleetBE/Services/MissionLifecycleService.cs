using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public enum MissionEventResult { Applied, Duplicate, Rejected }

public sealed class MissionLifecycleService(SmartFleetDbContext db)
{
    public async Task<MissionEventResult> HandleAsync(
        string robotCode, RobotMissionEvent message, CancellationToken ct = default)
    {
        if (message.TaskId <= 0 || message.AssignmentId <= 0 ||
            message.Event is not ("MISSION_STARTED" or "MISSION_COMPLETED" or "MISSION_FAILED"))
            return MissionEventResult.Rejected;

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // The robot row serializes dispatch, completion and messages from multiple API instances.
        var robotQuery = db.Database.IsSqlServer()
            ? db.Robots.FromSqlInterpolated($"SELECT * FROM dbo.Robots WITH (UPDLOCK, HOLDLOCK) WHERE RobotCode = {robotCode}")
            : db.Robots.Where(r => r.RobotCode == robotCode);
        var robot = await robotQuery.SingleOrDefaultAsync(ct);
        if (robot is null || !string.Equals(robot.RobotCode, robotCode, StringComparison.Ordinal))
            return MissionEventResult.Rejected;

        var assignment = await db.TaskAssignments.SingleOrDefaultAsync(a =>
            a.AssignmentId == message.AssignmentId && a.TaskId == message.TaskId && a.RobotId == robot.RobotId, ct);
        if (assignment is null) return MissionEventResult.Rejected;
        var mission = await db.Missions.SingleOrDefaultAsync(m => m.AssignmentId == assignment.AssignmentId, ct);
        if (mission is null || (message.MissionId.HasValue && message.MissionId != mission.MissionId))
            return MissionEventResult.Rejected;

        // Never regress terminal state or release the robot for an obsolete assignment.
        if (mission.MissionStatus is "COMPLETED" or "FAILED" or "CANCELLED")
            return message.Event == "MISSION_STARTED" ||
                (message.Event == "MISSION_COMPLETED" && mission.MissionStatus == "COMPLETED") ||
                (message.Event == "MISSION_FAILED" && mission.MissionStatus == "FAILED")
                ? MissionEventResult.Duplicate : MissionEventResult.Rejected;
        if (assignment.AssignmentStatus != "ACTIVE") return MissionEventResult.Rejected;
        var task = await db.TransportTasks.SingleAsync(t => t.TaskId == assignment.TaskId, ct);
        if (task.TaskStatus is not (TransportTaskStatuses.Assigned or TransportTaskStatuses.Executing))
            return MissionEventResult.Rejected;
        if (message.Event == "MISSION_STARTED" && mission.MissionStatus == "EXECUTING" && task.TaskStatus == TransportTaskStatuses.Executing)
            return MissionEventResult.Duplicate;

        var now = DateTime.UtcNow;
        var status = message.Event switch
        {
            "MISSION_STARTED" => "EXECUTING",
            "MISSION_COMPLETED" => "COMPLETED",
            _ => "FAILED"
        };
        var reason = string.IsNullOrWhiteSpace(message.Reason) ? message.Event : message.Reason[..Math.Min(500, message.Reason.Length)];
        if (mission.MissionStatus != status)
            db.MissionStatusHistories.Add(new MissionStatusHistory {
                MissionId = mission.MissionId, PreviousStatus = mission.MissionStatus, NewStatus = status,
                Source = "ROBOT", Reason = reason, ChangedAt = now });
        if (task.TaskStatus != status)
            db.TaskStatusHistories.Add(new TaskStatusHistory {
                TaskId = task.TaskId, PreviousStatus = task.TaskStatus, NewStatus = status, Reason = reason, ChangedAt = now });
        mission.MissionStatus = task.TaskStatus = status;
        mission.LastProgressAt = now;
        if (message.Event == "MISSION_STARTED")
        {
            mission.StartExecutionTime ??= now;
            task.StartedAt ??= now;
        }
        else
        {
            mission.EndExecutionTime = now;
            // If STARTED was lost, don't invent an execution start time.
            if (mission.StartExecutionTime.HasValue)
                mission.TotalDurationSeconds = (int)Math.Clamp((now - mission.StartExecutionTime.Value).TotalSeconds, 0, int.MaxValue);
            assignment.AssignmentStatus = status;
            assignment.EndedAt = now;
            if (status == "COMPLETED") task.CompletedAt = now;
            else mission.FailureReason = task.FailureReason = reason;

            var anotherAssignment = await db.TaskAssignments.AnyAsync(a => a.RobotId == robot.RobotId &&
                a.AssignmentId != assignment.AssignmentId && a.AssignmentStatus == "ACTIVE", ct);
            var maintenance = await db.RobotMaintenanceRecords.AnyAsync(m => m.RobotId == robot.RobotId &&
                (m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS"), ct);
            // A failure requires inspection. Preserve externally set maintenance/error states.
            var nextRobotStatus = status == "FAILED" ? "ERROR" : maintenance ? "MAINTENANCE" : "AVAILABLE";
            if (!anotherAssignment && robot.OperationalStatus == "BUSY")
            {
                db.RobotStatusHistories.Add(new RobotStatusHistory {
                    RobotId = robot.RobotId, PreviousOperationalStatus = robot.OperationalStatus,
                    NewOperationalStatus = nextRobotStatus, Reason = reason, ChangedAt = now });
                robot.OperationalStatus = nextRobotStatus;
                robot.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MissionEventResult.Applied;
    }
}
