using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public static class OperationalAlertService
{
    // Call within the caller's transaction. One open incident represents one alarm episode.
    public static async Task RaiseAsync(SmartFleetDbContext db, Robot robot, string type,
        string severity, string reason, DateTime now, long? taskId = null, long? missionId = null,
        CancellationToken ct = default)
    {
        if (await db.RobotIncidents.AnyAsync(i => i.RobotId == robot.RobotId &&
            i.IncidentType == type && !i.IsResolved && i.MissionId == missionId, ct)) return;
        var incident = new RobotIncident { RobotId = robot.RobotId, IncidentType = type,
            Severity = severity, Description = reason, CreatedAt = now, TaskId = taskId, MissionId = missionId };
        db.RobotIncidents.Add(incident);
        var notification = new Notification { NotificationCategory = "ROBOT_ALERT",
            Title = $"{robot.RobotCode}: {type}", Message = reason, Severity = severity,
            CreatedAt = now, RelatedRobotId = robot.RobotId, RelatedTaskId = taskId,
            RelatedMissionId = missionId, RelatedIncident = incident };
        var recipients = await db.UserRoles.Where(ur => ur.User.IsActive &&
            (ur.Role.RoleName == AppRoles.WarehouseOperator || ur.Role.RoleName == AppRoles.MaintenanceTechnician))
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
        foreach (var id in recipients) notification.NotificationRecipients.Add(new NotificationRecipient { UserId = id });
        db.Notifications.Add(notification);
    }
}
