using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController, Route("api/v1/robot-registry"), Authorize(Roles = AppRoles.Admin)]
public sealed class RobotRegistryController(RobotRegistryService service) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> Read(CancellationToken ct) => Ok(await service.Read(ct));
    [HttpPost] public Task<IActionResult> Create(RobotRegistryWrite request, CancellationToken ct) => Save(null, request, ct);
    [HttpPut("{id:int}")] public Task<IActionResult> Update(int id, RobotRegistryWrite request, CancellationToken ct) => Save(id, request, ct);
    private async Task<IActionResult> Save(int? id, RobotRegistryWrite request, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try { return Ok(new { robotId = await service.Save(id, request, actor, ct) }); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Robot not found." }); }
        catch (InvalidOperationException e) { return Conflict(new { message = e.Message }); }
        catch (DbUpdateException) { return Conflict(new { message = "Robot identity conflict or concurrent configuration change. Reload and retry." }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "40001" or "40P01" or "23505") { return Conflict(new { message = "Robot registration changed concurrently. Reload and retry." }); }
    }
}
