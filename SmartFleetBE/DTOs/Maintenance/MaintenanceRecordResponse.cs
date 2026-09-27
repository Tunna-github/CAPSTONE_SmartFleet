namespace SmartFleetBE.DTOs.Maintenance;

public sealed class MaintenanceRecordResponse
{
    public long MaintenanceId { get; set; }
    public int RobotId { get; set; }
    public string MaintenanceType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string MaintenanceStatus { get; set; } = string.Empty;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
