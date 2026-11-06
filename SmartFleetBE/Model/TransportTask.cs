
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    // NEW:
    // AUTO   -> automatic assignment (possibly scheduled)
    // MANUAL -> operator assigns a robot manually
    //
    // The database also defaults existing records to MANUAL.
    [Column("DispatchMode")]
    [StringLength(10)]
    public string DispatchMode { get; set; } = "MANUAL";

    public string? PackageCode { get; set; }

    public string? ItemDescription { get; set; }

    public decimal? PayloadWeightKg { get; set; }

    public int CreatedBy { get; set; }

    // Actual time the Task was created.
    public DateTime CreatedAt { get; set; }

    // Earliest time an AUTO task may be dispatched.
    // Stored as UTC in PostgreSQL.
    public DateTime? ScheduledTime { get; set; }

    public DateTime? QueuedAt { get; set; }

    // Actual time the Task was assigned to a robot.
    public DateTime? AssignedAt { get; set; }

    // Actual time execution started.
    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? FailureReason { get; set; }

    public string? CancellationReason { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Station DeliveryStation { get; set; } = null!;

    public virtual ICollection<Notification> Notifications
    { get; set; } = new List<Notification>();

    public virtual Station PickupStation { get; set; } = null!;

    public virtual ICollection<RobotIncident> RobotIncidents
    { get; set; } = new List<RobotIncident>();

    public virtual ICollection<TaskAssignment> TaskAssignments
    { get; set; } = new List<TaskAssignment>();

    public virtual ICollection<TaskStatusHistory> TaskStatusHistories
    { get; set; } = new List<TaskStatusHistory>();

    public virtual Warehouse Warehouse { get; set; } = null!;
}
