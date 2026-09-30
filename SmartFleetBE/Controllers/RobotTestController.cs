using Microsoft.AspNetCore.Mvc;
using MQTTnet;
using System.Text.Json;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/robot-test")]
public class RobotTestController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public RobotTestController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpPost("circle")]
    public async Task<IActionResult> RunCircle(
        CancellationToken cancellationToken)
    {
        // Mosquitto is running on the same Windows PC as .NET
        var brokerHost = "localhost";
        var brokerPort = 1883;

        // Must match the topic used by the Jetson
        var topic = "smartfleet/robot/R01/command";

        // This is exactly the JSON format already tested successfully
        var command = new
        {
            command = "EXECUTE_TASK",
            taskId = 1,
            assignmentId = 1,
            pattern = "CIRCLE"
        };

        var payload = JsonSerializer.Serialize(command);

        var factory = new MqttClientFactory();

        using var mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(brokerHost, brokerPort)
            .Build();

        await mqttClient.ConnectAsync(
            options,
            cancellationToken
        );

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();

        await mqttClient.PublishAsync(
            message,
            cancellationToken
        );

        await mqttClient.DisconnectAsync();

        return Ok(new
        {
            message = "Circle command sent to R01",
            topic,
            command
        });
    }
}