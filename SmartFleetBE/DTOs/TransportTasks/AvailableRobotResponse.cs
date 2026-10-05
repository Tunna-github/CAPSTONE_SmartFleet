namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class AvailableRobotResponse
{
    public int RobotId { get; set; }

    public string RobotCode { get; set; } = string.Empty;

    public decimal? BatteryPercent { get; set; }

    public string ConnectionStatus { get; set; } = string.Empty;

    public string OperationalStatus { get; set; } = string.Empty;
}