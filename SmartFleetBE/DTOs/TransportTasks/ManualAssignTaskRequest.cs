namespace SmartFleetBE.DTOs.TransportTasks
{
    public class ManualAssignTaskRequest
    {
        public int RobotId { get; set; }

        // Tạm thời dùng FIGURE_8 cho integration test
        public string MovementPattern { get; set; } = "FIGURE_8";
    }
}
