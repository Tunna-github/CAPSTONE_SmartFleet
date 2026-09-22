using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/transport-tasks")]
[Authorize(Roles = AppRoles.AdminOrWarehouseOperator)]
public sealed class TransportTasksController : ControllerBase
{
    private readonly ITransportTaskService _transportTaskService;

    public TransportTasksController(ITransportTaskService transportTaskService)
    {
        _transportTaskService = transportTaskService;
    }

    /// <summary>
    /// Returns transport tasks. ADMIN and WAREHOUSE_OPERATOR only.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<TransportTaskResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<TransportTaskResponse>>> GetTransportTasks(
        CancellationToken cancellationToken)
    {
        var tasks = await _transportTaskService.GetAllAsync(cancellationToken);
        return Ok(tasks);
    }
}
