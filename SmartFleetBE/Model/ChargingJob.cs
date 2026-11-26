namespace SmartFleetBE.Model;

// A transport task is complete before this device service workflow starts.
// The berth is reserved until a later camera-confirmed DEPARTURE, never a timer.
public sealed class ChargingJob
{
    public Guid Id { get; set; }
    public int RobotId { get; set; }
    public Robot Robot { get; set; } = null!;
    public long SourceMissionId { get; set; }
    public int? StationId { get; set; }
    public string State { get; set; } = "WAITING";
    public int Generation { get; set; }
    public int MapRevision { get; set; }
    public string? RouteJson { get; set; }
    public int NextWaypointIndex { get; set; }
    public string? LastEventId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastStoppedAt { get; set; }
    public DateTime? StopAcknowledgedAt { get; set; }
    public string? Reason { get; set; }
    public int Revision { get; set; }
}
