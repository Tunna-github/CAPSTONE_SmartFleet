using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class RobotStatusHistory
{
    public long RobotStatusHistoryId { get; set; }

    public int RobotId { get; set; }

    public string? PreviousConnectionStatus { get; set; }

    public string? NewConnectionStatus { get; set; }

    public string? PreviousOperationalStatus { get; set; }

    public string? NewOperationalStatus { get; set; }

    public string? Reason { get; set; }

    public DateTime ChangedAt { get; set; }

    public virtual Robot Robot { get; set; } = null!;
}
