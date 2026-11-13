using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartFleetBE.Constants;
using SmartFleetBE.Hubs;
using SmartFleetBE.Model;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/warehouse-maps")]
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.WarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
public sealed class WarehouseMapsController(
    SmartFleetDbContext db,
    IHubContext<FleetHub> hub,
    ILogger<WarehouseMapsController> logger) : ControllerBase
{
    // GET /api/v1/warehouse-maps/warehouses
    [HttpGet("warehouses")]
    public async Task<IActionResult> Warehouses(CancellationToken ct)
    {
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.WarehouseId)
            .Select(w => new
            {
                w.WarehouseId,
                w.WarehouseCode,
                w.WarehouseName
            })
            .ToListAsync(ct);
        return Ok(warehouses);
    }

    // GET /api/v1/warehouse-maps/1
    // Returns a snapshot that the UI can recover after reconnecting.
    [HttpGet("{warehouseId:int}")]
    public async Task<IActionResult> Snapshot(int warehouseId, CancellationToken ct)
    {
        var warehouse = await db.Warehouses.AsNoTracking()
            .Where(w => w.WarehouseId == warehouseId && w.IsActive)
            .Select(w => new { w.WarehouseId, w.WarehouseCode, w.WarehouseName })
            .SingleOrDefaultAsync(ct);
        if (warehouse is null) return NotFound(new { message = "Warehouse not found." });

        var markers = await db.ArucoMarkerMaps.AsNoTracking()
            .Where(m => m.WarehouseId == warehouseId)
            .OrderBy(m => m.GridRow).ThenBy(m => m.GridColumn)
            .Select(m => new
            {
                m.ArucoId, m.CellCode, m.GridRow, m.GridColumn, m.UpdatedAt
            }).ToListAsync(ct);

        var robots = await db.Robots.AsNoTracking()
            .Where(r => r.WarehouseId == warehouseId && r.IsActive)
            .OrderBy(r => r.RobotCode)
            .Select(r => new { r.RobotId, r.RobotCode, r.ConnectionStatus, r.OperationalStatus })
            .ToListAsync(ct);
        var robotIds = robots.Select(r => r.RobotId).ToArray();
        var lastScans = await db.RobotArucoPositions.AsNoTracking()
            .Where(p => robotIds.Contains(p.RobotId))
            .ToListAsync(ct);
        var scansByRobot = lastScans.ToDictionary(p => p.RobotId);
        var markersById = markers.ToDictionary(m => m.ArucoId);

        var robotPositions = robots.Select(r =>
        {
            scansByRobot.TryGetValue(r.RobotId, out var scan);
            var mapped = scan != null && markersById.TryGetValue(scan.ArucoId, out _);
            var marker = mapped ? markersById[scan!.ArucoId] : null;
            return new
            {
                r.RobotId,
                r.RobotCode,
                r.ConnectionStatus,
                r.OperationalStatus,
                ArucoId = scan?.ArucoId,
                CellCode = marker?.CellCode,
                GridRow = marker?.GridRow,
                GridColumn = marker?.GridColumn,
                SeenAt = scan?.SeenAt
            };
        }).ToList();

        return Ok(new
        {
            warehouse.WarehouseId,
            warehouse.WarehouseCode,
            warehouse.WarehouseName,
            markers,
            robots = robotPositions
        });
    }

    // PUT /api/v1/warehouse-maps/1/markers/1
    // Body: { "gridRow": 0, "gridColumn": 1 }  means A1.
    [HttpPut("{warehouseId:int}/markers/{arucoId:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> UpsertMarker(
        int warehouseId, int arucoId, [FromBody] UpsertArucoMarkerRequest request,
        CancellationToken ct)
    {
        if (arucoId < 0 || arucoId > 9999 || request.GridRow < 0 ||
            request.GridRow > 25 || request.GridColumn < 1 || request.GridColumn > 40)
            return BadRequest(new { message = "Marker ID must be 0..9999, row 0..25 and column 1..40." });

        if (!await db.Warehouses.AnyAsync(w => w.WarehouseId == warehouseId && w.IsActive, ct))
            return NotFound(new { message = "Warehouse not found." });

        if (await db.ArucoMarkerMaps.AnyAsync(m => m.WarehouseId == warehouseId &&
            m.ArucoId != arucoId && m.GridRow == request.GridRow &&
            m.GridColumn == request.GridColumn, ct))
            return Conflict(new { message = "Another ArUco ID already occupies that cell." });

        var code = $"{(char)('A' + request.GridRow)}{request.GridColumn}";
        var existing = await db.ArucoMarkerMaps.SingleOrDefaultAsync(
            m => m.WarehouseId == warehouseId && m.ArucoId == arucoId, ct);
        if (existing is null)
        {
            existing = new ArucoMarkerMap
            {
                WarehouseId = warehouseId,
                ArucoId = arucoId,
                GridRow = request.GridRow,
                GridColumn = request.GridColumn,
                CellCode = code,
                UpdatedAt = DateTime.UtcNow
            };
            db.ArucoMarkerMaps.Add(existing);
        }
        else
        {
            existing.GridRow = request.GridRow;
            existing.GridColumn = request.GridColumn;
            existing.CellCode = code;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            return Conflict(new { message = "Marker or map cell was configured by another user. Refresh and retry." });
        }
        await NotifyChangedAsync(warehouseId, ct);
        return Ok(new { existing.ArucoId, existing.CellCode, existing.GridRow, existing.GridColumn });
    }

    // DELETE /api/v1/warehouse-maps/1/markers/1
    [HttpDelete("{warehouseId:int}/markers/{arucoId:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> RemoveMarker(int warehouseId, int arucoId, CancellationToken ct)
    {
        var marker = await db.ArucoMarkerMaps.SingleOrDefaultAsync(
            m => m.WarehouseId == warehouseId && m.ArucoId == arucoId, ct);
        if (marker is null) return NotFound(new { message = "Marker mapping not found." });
        db.ArucoMarkerMaps.Remove(marker);
        await db.SaveChangesAsync(ct);
        await NotifyChangedAsync(warehouseId, ct);
        return NoContent();
    }

    private async Task NotifyChangedAsync(int warehouseId, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("MapConfigurationUpdated", new { warehouseId }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Marker map persisted but live map notification failed.");
        }
    }
}

public sealed class UpsertArucoMarkerRequest
{
    public int GridRow { get; set; }
    public int GridColumn { get; set; }
}
