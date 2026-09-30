namespace SmartFleetBE.DTOs.TransportTasks;

public sealed class ManualAssignTaskResponse
{
    public long TaskId { get; set; }

    public string TaskTrackingCode { get; set; } = string.Empty;

    public int RobotId { get; set; }

    public string RobotCode { get; set; } = string.Empty;

    public long AssignmentId { get; set; }

    public long MissionId { get; set; }

    public string MissionCode { get; set; } = string.Empty;

    public string TaskStatus { get; set; } = string.Empty;

    public string RobotStatus { get; set; } = string.Empty;

    public string MissionStatus { get; set; } = string.Empty;

    public string RobotCommand { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}