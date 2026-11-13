using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SmartFleetBE.Constants;
using SmartFleetBE.Hubs;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

// DEMO: Same REST endpoints as the DB version, but no DbContext is injected.
// Uses an in-memory store shared with MqttArucoPositionWorker.
[ApiController]
[Route("api/v1/warehouse-maps")]
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.WarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
public sealed class WarehouseMapsController(
    IHubContext<FleetHub> hub) : ControllerBase
{
    private static readonly MapDemoStore demo = MapDemoStore.Shared;
    [HttpGet("warehouses")]
    public IActionResult Warehouses() => Ok(demo.ListWarehouses());

    [HttpGet("{warehouseId:int}")]
    public IActionResult Snapshot(int warehouseId)
    {
        if (warehouseId != MapDemoStore.TestWarehouseId)
            return NotFound(new { message = "Only DEMO warehouse 1 is available in no-DB test mode." });
        return Ok(demo.GetSnapshot());
    }

    // PUT /api/v1/warehouse-maps/1/markers/1
    // {"gridRow":0,"gridColumn":1} => A1
    [HttpPut("{warehouseId:int}/markers/{arucoId:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> UpsertMarker(
        int warehouseId, int arucoId,
        [FromBody] DemoUpsertMarkerRequest request,
        CancellationToken ct)
    {
        if (warehouseId != MapDemoStore.TestWarehouseId)
            return NotFound(new { message = "Demo warehouse not found." });

        if (arucoId < 0 || arucoId > 9999 ||
            request.GridRow < 0 || request.GridRow > 25 ||
            request.GridColumn < 1 || request.GridColumn > 40)
            return BadRequest(new { message = "ArUco ID 0..9999, row 0..25, column 1..40." });

        if (!demo.TryUpsert(arucoId, request.GridRow, request.GridColumn,
                out var marker, out var conflict))
            return Conflict(new { message = conflict });

        await hub.Clients.All.SendAsync("MapConfigurationUpdated",
            new { warehouseId }, ct);
        return Ok(marker);
    }

    [HttpDelete("{warehouseId:int}/markers/{arucoId:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> RemoveMarker(
        int warehouseId, int arucoId, CancellationToken ct)
    {
        if (warehouseId != MapDemoStore.TestWarehouseId)
            return NotFound(new { message = "Demo warehouse not found." });

        if (!demo.Remove(arucoId))
            return NotFound(new { message = "ArUco marker mapping not found." });

        await hub.Clients.All.SendAsync("MapConfigurationUpdated",
            new { warehouseId }, ct);
        return NoContent();
    }
}

public sealed class DemoUpsertMarkerRequest
{
    public int GridRow { get; set; }
    public int GridColumn { get; set; }
}
