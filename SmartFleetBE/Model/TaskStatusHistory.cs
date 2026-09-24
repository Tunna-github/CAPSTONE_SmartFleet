using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class TaskStatusHistory
{
    public long TaskStatusHistoryId { get; set; }

    public long TaskId { get; set; }

    public string? PreviousStatus { get; set; }

    public string NewStatus { get; set; } = null!;

    public int? ChangedBy { get; set; }

    public string? Reason { get; set; }

    public DateTime ChangedAt { get; set; }

    public virtual User? ChangedByNavigation { get; set; }

    public virtual TransportTask Task { get; set; } = null!;
}
