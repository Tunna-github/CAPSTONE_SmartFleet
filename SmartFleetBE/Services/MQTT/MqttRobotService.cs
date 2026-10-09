using System.Text.Json;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public sealed class MqttRobotService(MqttService mqtt) : IMqttRobotService
{
    public Task SendExecuteTaskAsync(string robotCode, long taskId, long assignmentId,
        string pattern, CancellationToken cancellationToken = default)
        => mqtt.PublishAsync($"smartfleet/robot/{robotCode}/command",
            JsonSerializer.Serialize(new { command = "EXECUTE_TASK", taskId, assignmentId, pattern }), cancellationToken);
}
