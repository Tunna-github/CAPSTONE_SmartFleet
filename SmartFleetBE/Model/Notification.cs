using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class Notification
{
    public long NotificationId { get; set; }

    public string NotificationCategory { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string Message { get; set; } = null!;

    public string Severity { get; set; } = null!;

    public bool IsBroadcast { get; set; }

    public int? RelatedRobotId { get; set; }

    public long? RelatedTaskId { get; set; }

    public long? RelatedMissionId { get; set; }

    public long? RelatedIncidentId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<NotificationRecipient> NotificationRecipients { get; set; } = new List<NotificationRecipient>();

    public virtual RobotIncident? RelatedIncident { get; set; }

    public virtual Mission? RelatedMission { get; set; }

    public virtual Robot? RelatedRobot { get; set; }

    public virtual TransportTask? RelatedTask { get; set; }
}
