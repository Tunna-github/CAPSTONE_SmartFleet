namespace SmartFleetBE.Services.Interfaces;

public interface IMqttRobotService
{
    Task SendExecuteTaskAsync(
        string robotCode,
        long taskId,
        long assignmentId,
        string pattern,
        CancellationToken cancellationToken = default);
}