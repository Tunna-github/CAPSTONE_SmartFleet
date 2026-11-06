using System.Text.Json.Serialization;

namespace SmartFleetBE.DTOs;

public sealed class RobotLidarEvent
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("distanceCm")]
    public decimal? DistanceCm { get; set; }
}
