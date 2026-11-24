using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;
using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Controllers;

[ApiController, Authorize(Roles = AppRoles.AdminOrWarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
[Route("api/v1/missions")]
public sealed class MissionsController(MissionExecutionService service, FleetRealtimePublisher realtime,
    SmartFleetDbContext db, ILogger<MissionsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ViewsAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        await service.ViewAsync(id, ct) is { } mission ? Ok(mission) : NotFound(new { message = "Persisted mission not found." });

    [HttpPost("{id:long}/verifications"), Authorize(Roles = AppRoles.WarehouseOperator)]
    public async Task<IActionResult> Verify(long id, VerificationRequest request, CancellationToken ct)
    {
        try
        {
            var result = await service.VerifyAsync(id, int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), request, ct);
            // The response is the durable transaction result; broadcasts must not turn it into an apparent failure.
            try { await realtime.MissionUpdatedAsync(await db.Missions.Where(m => m.MissionId == id).Select(m => m.AssignmentId).SingleAsync(ct), ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Verification committed but realtime broadcast failed for {MissionId}", id); }
            return result.Accepted ? Ok(result) : UnprocessableEntity(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (Exception ex) when (MissionExecutionService.IsConflict(ex))
        { return Conflict(new { message = "Mission changed concurrently. Refresh and retry with the same request ID." }); }
    }
}
