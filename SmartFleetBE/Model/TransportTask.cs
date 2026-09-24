using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class TransportTask
{
    public long TaskId { get; set; }

    public string TaskTrackingCode { get; set; } = null!;

    public int WarehouseId { get; set; }

    public int PickupStationId { get; set; }

    public int DeliveryStationId { get; set; }

    public int PriorityLevel { get; set; }

    public string TaskStatus { get; set; } = null!;

    public string? PackageCode { get; set; }

    public string? ItemDescription { get; set; }

    public decimal? PayloadWeightKg { get; set; }

    public int CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ScheduledTime { get; set; }

    public DateTime? QueuedAt { get; set; }

    public DateTime? AssignedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? FailureReason { get; set; }

    public string? CancellationReason { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Station DeliveryStation { get; set; } = null!;

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual Station PickupStation { get; set; } = null!;

    public virtual ICollection<RobotIncident> RobotIncidents { get; set; } = new List<RobotIncident>();

    public virtual TaskAssignment? TaskAssignment { get; set; }

    public virtual ICollection<TaskStatusHistory> TaskStatusHistories { get; set; } = new List<TaskStatusHistory>();

    public virtual Warehouse Warehouse { get; set; } = null!;
}
