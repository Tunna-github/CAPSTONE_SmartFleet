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
            .Where(a => a.AssignmentStatus == "ACTIVE")
            .Select(a => new { a.TaskId, a.RobotId, a.Robot.RobotCode,
                a.Task.TaskTrackingCode, a.Task.TaskStatus, a.Robot.OperationalStatus,
                a.Robot.ConnectionStatus, missionStatus = a.Mission == null ? null : a.Mission.MissionStatus,
                intervention = db.TaskInterventions.FirstOrDefault(c => c.TaskId == a.TaskId) })
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
    [HttpPost("{taskId:long}/pause")]
    public Task<IActionResult> Pause(long taskId, InterventionRequest request, CancellationToken ct) =>
        Execute(taskId, "pause", request, ct);

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
