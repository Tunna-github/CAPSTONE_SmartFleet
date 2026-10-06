namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class CancelTransportTaskResponse
{
    public long TaskId { get; set; }

    public string TaskTrackingCode { get; set; }
        = string.Empty;

    public string PreviousTaskStatus { get; set; }
        = string.Empty;

    public string TaskStatus { get; set; }
        = string.Empty;

    public long? AssignmentId { get; set; }

    public string? AssignmentStatus { get; set; }

    public int? RobotId { get; set; }

    public string? RobotCode { get; set; }

    public string? RobotStatus { get; set; }

    public long? MissionId { get; set; }

    public string? MissionCode { get; set; }

    public string? MissionStatus { get; set; }

    public DateTime CancelledAt { get; set; }

    public string? CancellationReason { get; set; }

    public string Message { get; set; }
        = string.Empty;
}