using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class Mission
{
    public long MissionId { get; set; }

    public string MissionCode { get; set; } = null!;

    public long AssignmentId { get; set; }

    public string MissionStatus { get; set; } = null!;

    public DateTime? StartExecutionTime { get; set; }

    public DateTime? EndExecutionTime { get; set; }

    public DateTime? LastProgressAt { get; set; }

    public decimal TotalDistanceMeters { get; set; }

    public int TotalDurationSeconds { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual TaskAssignment Assignment { get; set; } = null!;

    public virtual ICollection<DeliveryVerification> DeliveryVerifications { get; set; } = new List<DeliveryVerification>();

    public virtual ICollection<MissionStatusHistory> MissionStatusHistories { get; set; } = new List<MissionStatusHistory>();

    public virtual ICollection<MissionWaypoint> MissionWaypoints { get; set; } = new List<MissionWaypoint>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual ICollection<RobotIncident> RobotIncidents { get; set; } = new List<RobotIncident>();
}
