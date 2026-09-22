using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class RobotMaintenanceRecord
{
    public long MaintenanceId { get; set; }

    public int RobotId { get; set; }

    public string MaintenanceType { get; set; } = null!;

    public string? Description { get; set; }

    public string MaintenanceStatus { get; set; } = null!;

    public DateTime? ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? CreatedBy { get; set; }

    public int? CompletedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User? CompletedByNavigation { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual Robot Robot { get; set; } = null!;
}
