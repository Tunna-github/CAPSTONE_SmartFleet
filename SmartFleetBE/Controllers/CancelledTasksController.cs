using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController, Route("api/v1/transport-tasks/{taskId:long}")]
[Authorize(Roles = AppRoles.WarehouseOperator)]
public sealed class CancelledTasksController(CancelledTaskService service) : ControllerBase
{
    [HttpPost("requeue")]
    public Task<IActionResult> Requeue(long taskId, RequeueCancelledTaskRequest request, CancellationToken ct) =>
        Execute(async actor => Ok(await service.Requeue(taskId, request, actor, ct)));

    [HttpDelete("cancelled")]
    public Task<IActionResult> Delete(long taskId, CancellationToken ct) => Execute(async actor => {
        await service.Delete(taskId, actor, ct); return NoContent();
    });

    private async Task<IActionResult> Execute(Func<int, Task<IActionResult>> action)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try { return await action(actor); }
        catch (KeyNotFoundException e) { return NotFound(new { message = e.Message }); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        catch (InvalidOperationException e) { return Conflict(new { message = e.Message }); }
        catch (Exception e) when (MissionExecutionService.IsConflict(e)) {
            return Conflict(new { message = "Task state changed concurrently. Reload before retrying." });
        }
    }
}
