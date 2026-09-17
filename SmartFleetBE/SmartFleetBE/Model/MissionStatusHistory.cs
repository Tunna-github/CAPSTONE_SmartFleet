using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class MissionStatusHistory
{
    public long MissionStatusHistoryId { get; set; }

    public long MissionId { get; set; }

    public string? PreviousStatus { get; set; }

    public string NewStatus { get; set; } = null!;

    public string Source { get; set; } = null!;

    public int? ChangedBy { get; set; }

    public string? Reason { get; set; }

    public DateTime ChangedAt { get; set; }

    public virtual User? ChangedByNavigation { get; set; }

    public virtual Mission Mission { get; set; } = null!;
}
