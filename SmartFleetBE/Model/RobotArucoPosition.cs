using System;

namespace SmartFleetBE.Model;

// Stores the LAST OBSERVED marker, not continuous odometry.
public sealed class RobotArucoPosition
{
    public int RobotId { get; set; }
    public int ArucoId { get; set; }
    public DateTime SeenAt { get; set; }
}
