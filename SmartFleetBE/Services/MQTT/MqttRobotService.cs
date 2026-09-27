using System.Text.Json;
using MQTTnet;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public class MqttRobotService : IMqttRobotService
{
    private readonly IConfiguration _configuration;

    public MqttRobotService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendExecuteTaskAsync(
        string robotCode,
        long taskId,
        long assignmentId,
        string pattern,
        CancellationToken cancellationToken = default)
    {
        var factory = new MqttClientFactory();
        using var mqttClient = factory.CreateMqttClient();

        var host = _configuration["Mqtt:Host"] ?? "localhost";

        var port = int.TryParse(
            _configuration["Mqtt:Port"],
            out var configuredPort)
            ? configuredPort
            : 1883;

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .Build();

        await mqttClient.ConnectAsync(
            options,
            cancellationToken
        );

        var command = new
        {
            command = "EXECUTE_TASK",
            taskId = taskId,
            assignmentId = assignmentId,
            pattern = pattern
        };

        var json = JsonSerializer.Serialize(command);

        var topic =
            $"smartfleet/robot/{robotCode}/command";

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .Build();

        await mqttClient.PublishAsync(
            message,
            cancellationToken
        );

        await mqttClient.DisconnectAsync();
    }
}