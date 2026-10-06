using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class CancelTransportTaskRequest
{
    [StringLength(500)]
    public string? Reason { get; set; }
}