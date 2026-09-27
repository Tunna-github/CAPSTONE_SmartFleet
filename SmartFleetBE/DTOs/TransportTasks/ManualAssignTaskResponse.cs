namespace SmartFleetBE.DTOs.TransportTasks
{
    public class ManualAssignTaskResponse
    {
        public long TaskId { get; set; }
        public string TaskTrackingCode { get; set; } = null!;

        public int RobotId { get; set; }
        public string RobotCode { get; set; } = null!;

        public long AssignmentId { get; set; }

        public string TaskStatus { get; set; } = null!;
        public string RobotStatus { get; set; } = null!;

        public string MovementPattern { get; set; } = null!;

        public string Message { get; set; } = null!;
    }
}
