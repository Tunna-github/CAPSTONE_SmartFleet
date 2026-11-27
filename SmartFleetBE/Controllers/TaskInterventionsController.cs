using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/task-interventions")]
public sealed class TaskInterventionsController(TaskInterventionService service,
    SmartFleetDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.TaskAssignments.AsNoTracking()
            .Where(a => a.AssignmentStatus == "ACTIVE" || a.AssignmentStatus=="FAILED"&&a.Task.TaskStatus=="QUEUED"&&a.Robot.OperationalStatus=="MAINTENANCE"&&
                !db.TaskAssignments.Any(active=>active.RobotId==a.RobotId&&active.AssignmentStatus=="ACTIVE")&&
                !db.DeliveryVerifications.Any(v=>a.Mission!=null&&v.MissionId==a.Mission.MissionId&&v.VerificationStage=="PICKUP"&&v.IsVerified))
            .Select(a => new { a.TaskId, a.AssignmentId,a.AssignmentStatus, a.RobotId, a.Robot.RobotCode,
                a.Task.TaskTrackingCode, a.Task.TaskStatus, a.Robot.OperationalStatus,
                a.Task.PickupStationId, pickupStationName = a.Task.PickupStation.StationName,
                a.Robot.ConnectionStatus, missionStatus = a.Mission == null ? null : a.Mission.MissionStatus,
                pickupVerified=db.DeliveryVerifications.Any(v=>a.Mission!=null&&v.MissionId==a.Mission.MissionId&&v.VerificationStage=="PICKUP"&&v.IsVerified),
                intervention = db.TaskInterventions.Where(c => c.TaskId == a.TaskId).Select(c=>new {
                    c.State,c.Paused,c.CommandAcknowledged,c.ResumeFailureReason,resumeSupported=c.PauseContextJson!=null }).FirstOrDefault() })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("{taskId:long}/cancel")]
    [Authorize(Roles = AppRoles.WarehouseOperator)]
    public Task<IActionResult> Cancel(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId, "cancel", request, ct);

    [HttpPost("{taskId:long}/request-cancel")]
    [Authorize(Roles = AppRoles.WarehouseOperator)]
    public Task<IActionResult> RequestCancel(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId, "request-cancel", request, ct);

    [HttpPost("{taskId:long}/force-cancel")]
    [Authorize(Roles = AppRoles.MaintenanceTechnician)]
    public Task<IActionResult> ForceCancel(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId, "force-cancel", request, ct);

    // Any authenticated role may stop motion. No business cancellation permission is implied.
    [HttpPost("{taskId:long}/recover-requeue")]
    [Authorize(Roles = AppRoles.MaintenanceTechnician)]
    public Task<IActionResult> RecoverRequeue(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId, "recover-requeue", request, ct);

    [HttpPost("{taskId:long}/pause")]
    public Task<IActionResult> Pause(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId, "pause", request, ct);

    [HttpPost("{taskId:long}/resume")]
    [Authorize(Roles=AppRoles.AdminOrWarehouseOperator+","+AppRoles.MaintenanceTechnician)]
    public Task<IActionResult> Resume(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId,"resume",request,ct);

    [HttpPost("{taskId:long}/return-to-charge")]
    [Authorize(Roles=AppRoles.MaintenanceTechnician)]
    public Task<IActionResult> ReturnToCharge(long taskId,InterventionRequest request,CancellationToken ct)=>
        Execute(taskId,"return-to-charge",request,ct);

    private async Task<IActionResult> Execute(long id, string action, InterventionRequest request, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        try { return Ok(await service.Act(id, action, request, userId, ct)); }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (Exception ex) when (IsConcurrency(ex))
        { return Conflict(new { message = "Robot/task state changed concurrently. Refresh and retry." }); }
    }

    private static bool IsConcurrency(Exception e) => e is Npgsql.PostgresException p &&
        p.SqlState is "40001" or "40P01" or "23505" || e.InnerException != null && IsConcurrency(e.InnerException);
}
