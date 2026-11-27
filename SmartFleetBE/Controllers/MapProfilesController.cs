using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;
using System.Security.Claims;

namespace SmartFleetBE.Controllers;

[ApiController, Route("api/v1/map-profiles")]
[Authorize(Roles = AppRoles.AdminOrWarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
public sealed class MapProfilesController(IConfiguration config, MapSwitchService switching, SmartFleetDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!config.GetValue<bool>("SharedDatabase:Enabled")) return Ok(new { shared = false, activeKey = "local", profiles = Array.Empty<object>() });
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            SELECT p."Key",p."DisplayName",p."SceneId",p."IsSelected",m."WarehouseID",w."WarehouseName"
            FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID"
            JOIN "Warehouses" w ON w."WarehouseID"=m."WarehouseID" ORDER BY p."Key"
            """, connection);
        var profiles = new List<object>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) profiles.Add(new { key = r.GetString(0), name = r.GetString(1), sceneId = r.GetString(2), isActive = r.GetBoolean(3), warehouseId = r.GetInt32(4), warehouseName = r.GetString(5) });
        return Ok(new { shared = true, activeKey = config["Deployment:MapKey"], profiles,
            selection = await switching.Read(ct) });
    }

    [HttpPost("select"), Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Select(MapSwitchWrite request, CancellationToken ct)
    {
        try { return Accepted(await switching.Request(request, int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct)); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }
}
