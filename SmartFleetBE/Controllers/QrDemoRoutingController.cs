using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

// Test-only: configures QR locations for QUEUED tasks without a database migration.
[ApiController]
[Route("api/v1/demo-routing")]
[Authorize(Roles = AppRoles.AdminOrWarehouseOperator)]
public sealed class QrDemoRoutingController(
    SmartFleetDbContext db,
    QrDemoRouteService routes,
    IConfiguration configuration) : ControllerBase
{
    public sealed class ConfigureTaskQrRequest
    {
        public int PickupArUcoId { get; set; }
        public int DeliveryArUcoId { get; set; }
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new
    {
        enabled = configuration.GetValue<bool>("DemoRoute:Enabled"),
        mode = "ONE_ROBOT_POSITION_BASED_WEBOTS_DEMO",
        pickupVerificationEnabled = false,
        intersectionReservationsEnabled = false
    });

    [HttpPost("tasks/{taskId:long}/qr")]
    public async Task<IActionResult> ConfigureTask(long taskId,
        [FromBody] ConfigureTaskQrRequest request, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("DemoRoute:Enabled"))
            return StatusCode(503, new { message = "Enable DemoRoute:Enabled for the local Webots demo." });
        var task = await db.TransportTasks.AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => new { t.TaskId, t.TaskStatus, t.DispatchMode })
            .SingleOrDefaultAsync(ct);
        if (task == null) return NotFound(new { message = "Transport task not found." });
        if (task.TaskStatus != "QUEUED" || task.DispatchMode != "MANUAL")
            return Conflict(new { message = "For this pilot, choose MANUAL dispatch and configure QR markers while the task is QUEUED." });
        try
        {
            routes.ConfigureTask(taskId, request.PickupArUcoId, request.DeliveryArUcoId);
            return Ok(new { taskId, request.PickupArUcoId, request.DeliveryArUcoId, configured = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("tasks/{taskId:long}/qr")]
    public IActionResult GetTaskQr(long taskId)
    {
        var value = routes.GetTaskSelection(taskId);
        return value == null ? NotFound(new { message = "No QR configuration for task." })
            : Ok(new { taskId, value.PickupArUcoId, value.DeliveryArUcoId });
    }

    // Canonical, directional QR graph derived from saved Warehouse Map links.
    [HttpGet("map/graph")]
    public IActionResult GetMapGraph() => Ok(routes.GetDirectedQrGraph());

    [HttpGet("map/validation")]
    public IActionResult ValidateMap() => Ok(routes.GetMapReadiness());

    [HttpGet("robots/positions")]
    public IActionResult Positions() => Ok(routes.GetRobotPositions());

    [HttpGet("robots/localizations")]
    public IActionResult Localizations() => Ok(routes.GetRobotLocalizations());

    [HttpGet("missions")]
    public IActionResult Missions() => Ok(routes.GetMissions());

    [HttpGet("missions/{missionId:long}")]
    public IActionResult Mission(long missionId)
    {
        var mission = routes.GetMission(missionId);
        return mission == null ? NotFound(new { message = "Mission not found in current Backend demo process." })
            : Ok(mission);
    }
}
