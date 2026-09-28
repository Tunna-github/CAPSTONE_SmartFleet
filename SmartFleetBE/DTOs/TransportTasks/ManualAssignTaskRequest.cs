using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class ManualAssignTaskRequest
{
    [Range(1, int.MaxValue)]
    public int RobotId { get; set; }
}