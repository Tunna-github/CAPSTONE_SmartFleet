using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController, Authorize]
[Route("api/v1/system/health")]
public sealed class SystemHealthController(SmartFleetDbContext db, MqttService mqtt,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = DateTime.UtcNow; var cutoff = now.AddSeconds(-RobotPresencePolicy.TimeoutSeconds(configuration));
        try
        {
            var robotCount = await db.Robots.CountAsync(r => r.IsActive, ct);
            var onlineRobotCount = await db.Robots.CountAsync(r => r.IsActive && r.ConnectionStatus == "ONLINE" &&
                r.LastHeartbeatTime >= cutoff && r.LastTelemetryTime >= cutoff, ct);
            return Ok(new { databaseConnected = true, robotCount, onlineRobotCount,
                mqtt = !configuration.GetValue<bool>("Mqtt:EnableWorkers") ? "DISABLED" : mqtt.IsPublisherConnected ? "CONNECTED" : "DISCONNECTED",
                measuredAtUtc = now });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(503, new { databaseConnected = false, mqtt = mqtt.IsPublisherConnected ? "CONNECTED" : "DISCONNECTED", measuredAtUtc = now });
        }
    }
}
