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
    IConfiguration configuration, MissionExecutionService execution) : ControllerBase
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
        mode = "QR_FLEET_WEBOTS_PILOT",
        pickupVerificationEnabled = true,
        deliveryVerificationEnabled = true,
        missionPersistenceEnabled = true,
        intersectionReservationsEnabled = configuration.GetValue<bool>("Navigation:Enabled")
    });

    [HttpPost("tasks/{taskId:long}/qr")]
    public async Task<IActionResult> ConfigureTask(long taskId,
        [FromBody] ConfigureTaskQrRequest request, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("DemoRoute:Enabled"))
            return StatusCode(503, new { message = "Enable DemoRoute:Enabled for the local Webots demo." });
        var task = await db.TransportTasks.AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => new { t.TaskId, t.TaskStatus, t.DispatchMode, t.WarehouseId })
            .SingleOrDefaultAsync(ct);
        if (task == null) return NotFound(new { message = "Transport task not found." });
        if (!routes.SupportsWarehouse(task.WarehouseId))
            return Conflict(new { message = "Task warehouse does not match the selected navigation map warehouse." });
        if (task.TaskStatus != "QUEUED" || task.DispatchMode != "MANUAL")
            return Conflict(new { message = "For this pilot, choose MANUAL dispatch and configure QR markers while the task is QUEUED." });
        try
        {
            var stored = await db.TransportTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId, ct);
            var mapped = await TaskLocationPolicy.ResolveRouteAsync(db, stored.WarehouseId, stored.PickupStationId, stored.DeliveryStationId, routes, ct);
            if (mapped.PickupArUcoId != request.PickupArUcoId || mapped.DeliveryArUcoId != request.DeliveryArUcoId)
                return BadRequest(new { message = "Task QR must match its stations. Admin manages station mappings in Warehouse & Stations." });
            routes.ConfigureTask(taskId, mapped.PickupArUcoId, mapped.DeliveryArUcoId);
            return Ok(new { taskId, request.PickupArUcoId, request.DeliveryArUcoId, configured = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("tasks/{taskId:long}/qr")]
    public async Task<IActionResult> GetTaskQr(long taskId, CancellationToken ct)
    {
        var task = await db.TransportTasks.AsNoTracking().SingleOrDefaultAsync(t => t.TaskId == taskId, ct);
        if (task == null) return NotFound(new { message = "Task not found." });
        if (await db.TaskAssignments.AnyAsync(a => a.TaskId == taskId, ct))
            return Conflict(new { message = "A dispatched task uses its mission's persisted frozen route. Read /api/v1/missions." });
        try {
            var value = await TaskLocationPolicy.ResolveRouteAsync(db, task.WarehouseId, task.PickupStationId, task.DeliveryStationId, routes, ct);
            return Ok(new { taskId, value.PickupArUcoId, value.DeliveryArUcoId });
        } catch (ArgumentException e) { return Conflict(new { message = e.Message }); }
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
    public async Task<IActionResult> Missions(CancellationToken ct) => Ok(await execution.ViewsAsync(ct));

    [HttpGet("missions/{missionId:long}")]
    public async Task<IActionResult> Mission(long missionId, CancellationToken ct)
    {
        var mission = await execution.ViewAsync(missionId, ct);
        return mission == null ? NotFound(new { message = "Persisted mission not found." })
            : Ok(mission);
    }
}
