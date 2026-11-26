using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController, Route("api/v1/charging")]
[Authorize(Roles=AppRoles.Admin+","+AppRoles.WarehouseOperator+","+AppRoles.MaintenanceTechnician)]
public sealed class ChargingController(SmartFleetDbContext db, ChargingService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { enabled=service.Enabled, minimumBatteryPercent=20,
        jobs=await db.ChargingJobs.AsNoTracking().Where(j=>j.State!="COMPLETED").OrderBy(j=>j.CreatedAt).Select(j=>new {
            j.Id,j.RobotId,j.Robot.RobotCode,j.SourceMissionId,j.StationId,j.State,j.NextWaypointIndex,j.MapRevision,j.Reason,
            j.CreatedAt,j.UpdatedAt,j.StopAcknowledgedAt,j.Robot.BatteryPercent,j.RouteJson,
            hasActiveAssignment=db.TaskAssignments.Any(a=>a.RobotId==j.RobotId && a.AssignmentStatus=="ACTIVE") }).ToListAsync(ct) });
    public sealed record ControlRequest(string Reason, bool PhysicalSafetyConfirmed=false);
    [HttpPost("{id:guid}/pause")]
    public Task<IActionResult> Pause(Guid id,ControlRequest request,CancellationToken ct) => Control(id,request,false,ct);
    [HttpPost("{id:guid}/resume"),Authorize(Roles=AppRoles.MaintenanceTechnician)]
    public Task<IActionResult> Resume(Guid id,ControlRequest request,CancellationToken ct) => Control(id,request,true,ct);
    [HttpPost("{id:guid}/release-recovered"),Authorize(Roles=AppRoles.MaintenanceTechnician)]
    public async Task<IActionResult> ReleaseRecovered(Guid id,ControlRequest request,CancellationToken ct)
    {
        try {await service.ReleaseRecoveredDeparture(id,int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),request.Reason,request.PhysicalSafetyConfirmed,ct);return Ok();}
        catch(KeyNotFoundException){return NotFound();}
        catch(ArgumentException ex){return BadRequest(new {message=ex.Message});}
        catch(InvalidOperationException ex){return Conflict(new {message=ex.Message});}
        catch(Exception ex) when(MissionExecutionService.IsConflict(ex)){return Conflict(new {message="Charging state changed; reload before releasing."});}
    }
    private async Task<IActionResult> Control(Guid id,ControlRequest r,bool resume,CancellationToken ct)
    {
        try {
            var actor=int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await service.Control(id,resume,actor,r.Reason,r.PhysicalSafetyConfirmed,ct); return Ok();
        }
        catch(KeyNotFoundException) { return NotFound(); }
        catch(ArgumentException ex) { return BadRequest(new {message=ex.Message}); }
        catch(InvalidOperationException ex) { return Conflict(new {message=ex.Message}); }
        catch(DbUpdateConcurrencyException) { return Conflict(new {message="Charging state changed. Reload before retrying."}); }
        catch(Exception ex) when(MissionExecutionService.IsConflict(ex)) { return Conflict(new {message="Concurrent charging safety update. Retry the control request."}); }
    }
}
