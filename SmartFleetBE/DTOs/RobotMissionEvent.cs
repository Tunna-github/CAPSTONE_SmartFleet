using System.Text.Json.Serialization;

namespace SmartFleetBE.DTOs;

public sealed class RobotMissionEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;
    public long TaskId { get; set; }
    public long AssignmentId { get; set; }
    public long? MissionId { get; set; }
    public string? EventId { get; set; }
    public string? Reason { get; set; }
}
