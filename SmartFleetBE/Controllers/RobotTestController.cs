using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/robot-test")]
public class RobotTestController : ControllerBase
{
    private readonly SmartFleetBE.Services.MqttService _mqtt;

    public RobotTestController(SmartFleetBE.Services.MqttService mqtt)
    {
        _mqtt = mqtt;
    }

    [HttpPost("circle")]
    public async Task<IActionResult> RunCircle(
        CancellationToken cancellationToken)
    {

        // Must match the topic used by the Jetson
        var topic = "smartfleet/robot/R01/command";

        // No hardcoded task/assignment IDs: a test move must not complete a real task.
        var command = new
        {
            command = "EXECUTE_TASK",
            testMode = true,
            testCommandId = Guid.NewGuid().ToString("N"),
            pattern = "CIRCLE"
        };

        var payload = JsonSerializer.Serialize(command);

        await _mqtt.PublishAsync(topic, payload, cancellationToken);

        return Ok(new
        {
            message = "Circle command sent to R01",
            topic,
            command
        });
    }
}
