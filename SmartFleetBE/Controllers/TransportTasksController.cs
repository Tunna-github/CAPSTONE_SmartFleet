using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Services.Interfaces;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/transport-tasks")]
[Authorize(Roles = AppRoles.WarehouseOperator)]
public sealed class TransportTasksController : ControllerBase
{
    private readonly ITransportTaskService _transportTaskService;

    public TransportTasksController(ITransportTaskService transportTaskService)
    {
        _transportTaskService = transportTaskService;
    }

    /// <summary>
    /// Returns all transport tasks for the warehouse operator.
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

    /// <summary>
    /// Returns one transport task by id.
    /// </summary>
    [HttpGet("{taskId:long}")]
    [ProducesResponseType(typeof(TransportTaskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TransportTaskResponse>> GetTransportTaskById(
        long taskId,
        CancellationToken cancellationToken)
    {
        var task = await _transportTaskService.GetByIdAsync(taskId, cancellationToken);

        if (task is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Transport task not found",
                $"Transport task {taskId} was not found."));
        }

        return Ok(task);
    }

    /// <summary>
    /// Creates a PENDING transport task. The creator is taken from the JWT.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TransportTaskResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TransportTaskResponse>> CreateTransportTask(
        [FromBody] CreateTransportTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(CreateProblem(
                StatusCodes.Status401Unauthorized,
                "Invalid access token",
                "The access token does not contain a valid user identifier."));
        }

        var result = await _transportTaskService.CreateAsync(
            request,
            userId,
            cancellationToken);

        if (result.Status == TransportTaskResultStatus.ValidationFailed)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid transport task",
                result.Error));
        }

        var createdTask = result.Value!;

        return CreatedAtAction(
            nameof(GetTransportTaskById),
            new { taskId = createdTask.TaskId },
            createdTask);
    }

    /// <summary>
    /// Fully updates the editable fields of a PENDING transport task.
    /// </summary>
    [HttpPut("{taskId:long}")]
    [ProducesResponseType(typeof(TransportTaskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TransportTaskResponse>> UpdateTransportTask(
        long taskId,
        [FromBody] UpdateTransportTaskRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _transportTaskService.UpdateAsync(
            taskId,
            request,
            cancellationToken);

        return result.Status switch
        {
            TransportTaskResultStatus.Success => Ok(result.Value),
            TransportTaskResultStatus.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Transport task not found",
                result.Error)),
            TransportTaskResultStatus.ValidationFailed => BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid transport task",
                result.Error)),
            TransportTaskResultStatus.Conflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "Transport task cannot be updated",
                result.Error)),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// Deletes a PENDING transport task that has not entered the operational lifecycle.
    /// </summary>
    [HttpDelete("{taskId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteTransportTask(
        long taskId,
        CancellationToken cancellationToken)
    {
        var result = await _transportTaskService.DeleteAsync(taskId, cancellationToken);

        return result.Status switch
        {
            TransportTaskResultStatus.Success => NoContent(),
            TransportTaskResultStatus.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "Transport task not found",
                result.Error)),
            TransportTaskResultStatus.Conflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "Transport task cannot be deleted",
                result.Error)),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        return int.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out userId);
    }

    private static ProblemDetails CreateProblem(
        int status,
        string title,
        string? detail)
    {
        return new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
    }
}
