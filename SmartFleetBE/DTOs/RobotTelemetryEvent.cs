using System.Text.Json.Serialization;

namespace SmartFleetBE.DTOs;

public sealed class RobotTelemetryEvent
{
    [JsonPropertyName("batteryPercent")]
    public decimal? BatteryPercent { get; set; }
    [JsonPropertyName("batteryVoltage")]
    public decimal? BatteryVoltage { get; set; }
    [JsonPropertyName("positionX")]
    public decimal? PositionX { get; set; }
    [JsonPropertyName("positionY")]
    public decimal? PositionY { get; set; }
    [JsonPropertyName("heading")]
    public decimal? Heading { get; set; }
    [JsonPropertyName("linearVelocity")]
    public decimal? LinearVelocity { get; set; }
    [JsonPropertyName("angularVelocity")]
    public decimal? AngularVelocity { get; set; }
    [JsonPropertyName("cpuTemperature")]
    public decimal? CpuTemperature { get; set; }
    [JsonPropertyName("wifiSignalDbm")]
    public decimal? WifiSignalDbm { get; set; }
}
