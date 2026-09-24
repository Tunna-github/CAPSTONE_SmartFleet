namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class TransportTaskResponse
{
    public long TaskId { get; set; }
    public string TaskTrackingCode { get; set; } = string.Empty;

    public int WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;

    public int PickupStationId { get; set; }
    public string PickupStationName { get; set; } = string.Empty;

    public int DeliveryStationId { get; set; }
    public string DeliveryStationName { get; set; } = string.Empty;

    public int PriorityLevel { get; set; }
    public string TaskStatus { get; set; } = string.Empty;

    public string? PackageCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal? PayloadWeightKg { get; set; }

    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledTime { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public string? FailureReason { get; set; }
    public string? CancellationReason { get; set; }
}
