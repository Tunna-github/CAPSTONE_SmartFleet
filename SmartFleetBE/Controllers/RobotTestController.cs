using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers
{
    [ApiController]
    [Route("api/robots")]
    public class RobotTestController : ControllerBase
    {
        private readonly MqttService _mqttService;

        public RobotTestController(MqttService mqttService)
        {
            _mqttService = mqttService;
        }

        [HttpPost("R01/test-move")]
        public async Task<IActionResult> TestMove(
            CancellationToken cancellationToken)
        {
            const string topic = "smartfleet/robot/R01/command";
            const string command = "TEST_MOVE";

            await _mqttService.PublishAsync(
                topic,
                command,
                cancellationToken);

            return Ok(new
            {
                robotId = "R01",
                command,
                topic,
                message = "Test move command published successfully."
            });
        }
    }
}