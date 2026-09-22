using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class MissionWaypoint
{
    public long WaypointId { get; set; }

    public long MissionId { get; set; }

    public int SequenceNumber { get; set; }

    public int? StationId { get; set; }

    public decimal CoordX { get; set; }

    public decimal CoordY { get; set; }

    public decimal YawAngle { get; set; }

    public string WaypointAction { get; set; } = null!;

    public bool IsReached { get; set; }

    public DateTime? ReachedAt { get; set; }

    public virtual Mission Mission { get; set; } = null!;

    public virtual Station? Station { get; set; }
}
