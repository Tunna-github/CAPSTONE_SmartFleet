using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class CreateTransportTaskRequest
{
    [Range(1, int.MaxValue)]
    public int WarehouseId { get; set; }

    [Range(1, int.MaxValue)]
    public int PickupStationId { get; set; }

    [Range(1, int.MaxValue)]
    public int DeliveryStationId { get; set; }

    // 1 = LOW, 2 = MEDIUM, 3 = HIGH
    [Range(1, 3)]
    public int PriorityLevel { get; set; } = 2;

    [StringLength(80)]
    public string? PackageCode { get; set; }

    [StringLength(500)]
    public string? ItemDescription { get; set; }

    [Range(typeof(decimal), "0.01", "999999.99")]
    public decimal? PayloadWeightKg { get; set; }

    public DateTime? ScheduledTime { get; set; }
}
