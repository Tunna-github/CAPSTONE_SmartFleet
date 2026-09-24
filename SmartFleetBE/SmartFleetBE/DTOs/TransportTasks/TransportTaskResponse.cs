namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class TransportTaskResponse
{
    public long TaskId { get; set; }
    public string TaskTrackingCode { get; set; } = string.Empty;
    public int WarehouseId { get; set; }
    public int PickupStationId { get; set; }
    public int DeliveryStationId { get; set; }
    public int PriorityLevel { get; set; }
    public string TaskStatus { get; set; } = string.Empty;
    public string? PackageCode { get; set; }
    public DateTime CreatedAt { get; set; }
}
