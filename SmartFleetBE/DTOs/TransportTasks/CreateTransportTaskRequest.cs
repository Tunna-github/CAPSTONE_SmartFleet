
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

    // Existing backend convention:
    // 1 = LOW, 2 = MEDIUM, 3 = HIGH
    [Range(1, 3)]
    public int PriorityLevel { get; set; } = 2;

    [StringLength(80)]
    public string? PackageCode { get; set; }

    [StringLength(500)]
    public string? ItemDescription { get; set; }

    // Required for both AUTO and MANUAL to safely dispatch.
    [Required(ErrorMessage = "PayloadWeightKg is required for dispatch.")]
    [Range(typeof(decimal), "0.01", "999999.99")]
    public decimal? PayloadWeightKg { get; set; }

    public DateTime? ScheduledTime { get; set; }

    // Safe default for existing clients.
    // MANUAL: create QUEUED task, operator assigns later.
    // AUTO: search eligible robots immediately.
    [Required]
    [RegularExpression(
        "^(AUTO|MANUAL)$",
        ErrorMessage = "DispatchMode must be AUTO or MANUAL.")]
    public string DispatchMode { get; set; } = "MANUAL";
}
