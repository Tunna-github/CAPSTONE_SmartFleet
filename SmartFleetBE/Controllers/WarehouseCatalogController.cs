using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;
[ApiController, Route("api/v1/warehouse-catalog")]
[Authorize(Roles = AppRoles.AdminOrWarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
public sealed class WarehouseCatalogController(StationCatalogService service) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> Read(CancellationToken ct) => Ok(await service.Read(ct));
    [HttpPost("warehouses"), Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> CreateWarehouse(WarehouseWrite r, CancellationToken ct) => Write(async u => new { id = await service.SaveWarehouse(null, r, u, ct) });
    [HttpPut("warehouses/{id:int}"), Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> UpdateWarehouse(int id, WarehouseWrite r, CancellationToken ct) => Write(async u => new { id = await service.SaveWarehouse(id, r, u, ct) });
    [HttpPost("zones"), Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> CreateZone(ZoneWrite r, CancellationToken ct) => Write(async u => new { id = await service.SaveZone(null, r, u, ct) });
    [HttpPut("zones/{id:int}"), Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> UpdateZone(int id, ZoneWrite r, CancellationToken ct) => Write(async u => new { id = await service.SaveZone(id, r, u, ct) });
    [HttpPost("stations"), Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> CreateStation(StationWrite r, CancellationToken ct) => Write(async u => new { id = await service.SaveStation(null, r, u, ct) });
    [HttpPut("stations/{id:int}"), Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> UpdateStation(int id, StationWrite r, CancellationToken ct) => Write(async u => new { id = await service.SaveStation(id, r, u, ct) });
    [HttpPost("stations/{id:int}/berth"), Authorize(Roles = AppRoles.AdminOrMaintenanceTechnician)]
    public Task<IActionResult> Berth(int id, BerthWrite r, CancellationToken ct) => Write(async u => { await service.Berth(id, r, u, ct); return new { saved = true }; });
    private async Task<IActionResult> Write(Func<int, Task<object>> action)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Unauthorized();
        try { return Ok(await action(id)); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Catalog item not found." }); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        catch (InvalidOperationException e) { return Conflict(new { message = e.Message }); }
        catch (DbUpdateException) { return Conflict(new { message = "Catalog constraint or concurrent update conflict. Reload; verify codes and checkpoint mapping." }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "40001" or "40P01" or "23505" or "23514") { return Conflict(new { message = "Catalog changed concurrently or violates a mapping constraint. Reload and retry." }); }
    }
}
