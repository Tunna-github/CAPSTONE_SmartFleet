using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/navigation")]
[Authorize(Roles = AppRoles.AdminOrWarehouseOperator)]
public sealed class NavigationController(
    NavigationCoordinator coordinator,
    SmartFleetDbContext db,
    MqttService mqtt,
    ILogger<NavigationController> logger) : ControllerBase
{
    [HttpGet("map")]
    public ActionResult<WarehouseMapDocument> GetMap() => Ok(coordinator.GetMap());

    [HttpPut("map")]
    [Authorize(Roles = AppRoles.Admin)]
    [RequestSizeLimit(262144)]
    public IActionResult PutMap([FromBody] WarehouseMapDocument map)
    {
        var result = coordinator.ReplaceMap(map);
        if (!result.Success) return Error(result);
        return Ok(coordinator.GetMap());
    }

    // Useful for FE point painting without submitting 504 cells each time.
    [HttpPut("map/cells/{row:int}/{col:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public IActionResult PutMapCell(int row, int col, [FromBody] MapCellUpdateRequest request)
    {
        var map = coordinator.GetMap();
        if (row < 0 || row >= map.Rows || col < 0 || col >= map.Columns)
            return BadRequest(new { message = "Cell coordinates out of bounds." });
        if (request.Cell == null)
            return BadRequest(new { message = "Missing cell." });
        if (request.Revision != map.Revision)
            return Conflict(new { message = "Stale map revision." });
        map.Cells[row][col] = request.Cell;
        var result = coordinator.ReplaceMap(map);
        if (!result.Success) return Error(result);
        return Ok(coordinator.GetMap());
    }

    [HttpPut("map/links")]
    [Authorize(Roles = AppRoles.Admin)]
    public IActionResult PutMapLinks([FromBody] MapLinksUpdateRequest request)
    {
        var map = coordinator.GetMap();
        if (request.Revision != map.Revision)
            return Conflict(new { message = "Stale map revision." });
        if (request.Links == null)
            return BadRequest(new { message = "Missing links." });
        map.Links = request.Links;
        var result = coordinator.ReplaceMap(map);
        if (!result.Success) return Error(result);
        return Ok(coordinator.GetMap());
    }

    [HttpGet("intersections")]
    public ActionResult<IReadOnlyList<IntersectionTrafficView>> GetIntersections() =>
        Ok(coordinator.GetIntersections());

    [HttpGet("intersections/{arucoId:int}")]
    public ActionResult<IntersectionTrafficView> GetIntersection(int arucoId)
    {
        var intersection = coordinator.GetIntersection(arucoId);
        return intersection == null ? NotFound(new { message = "Unknown intersection." }) : Ok(intersection);
    }

    [HttpPut("intersections/{arucoId:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public IActionResult PutIntersection(int arucoId, [FromBody] IntersectionSelectionRequest request)
    {
        var result = coordinator.MarkIntersection(arucoId, request.IsIntersection);
        if (!result.Success) return Error(result);
        return Ok(new
        {
            arucoId,
            request.IsIntersection,
            mapRevision = coordinator.GetMap().Revision
        });
    }

    [HttpGet("robots/configs")]
    public IActionResult GetRobotConfigs() => Ok(coordinator.GetRobotConfigs());

    [HttpGet("robots/{robotCode}/config")]
    public async Task<IActionResult> GetRobotConfig(string robotCode, CancellationToken ct)
    {
        if (!await RobotExists(robotCode, ct))
            return NotFound(new { message = "Robot does not exist or is inactive." });
        return Ok(new { robotCode, config = coordinator.GetRobotConfig(robotCode) });
    }

    [HttpPut("robots/{robotCode}/config")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> PutRobotConfig(
        string robotCode, [FromBody] RobotNavigationConfig config, CancellationToken ct)
    {
        if (!await RobotExists(robotCode, ct))
            return NotFound(new { message = "Robot does not exist or is inactive." });
        var result = coordinator.SetRobotConfig(robotCode, config);
        if (!result.Success) return Error(result);
        try
        {
            await NavigationMqttDispatch.PublishAsync(mqtt, result.Directives, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Saved navigation config, but MQTT command delivery failed.");
            return StatusCode(503, new
            {
                message = "Configuration saved, but MQTT delivery failed. The robot must fail closed."
            });
        }
        return Ok(new { robotCode, config = coordinator.GetRobotConfig(robotCode) });
    }

    // Manual intervention only. Never automatically release an unconfirmed crossing.
    [HttpPost("intersections/{arucoId:int}/physical-clear")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> PhysicalClear(
        int arucoId, [FromBody] ManualClearRequest request, CancellationToken ct)
    {
        var result = coordinator.ManuallyClearIntersection(
            arucoId, request.PhysicalAreaConfirmedClear, request.Reason);
        if (!result.Success) return Error(result);
        try
        {
            await NavigationMqttDispatch.PublishAsync(mqtt, result.Directives, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Physical clearance recorded but MQTT STOP failed.");
            return StatusCode(503, new
            {
                message = "Clearance recorded; not all STOP directives were delivered. Verify motors physically."
            });
        }
        return Ok(coordinator.GetIntersection(arucoId));
    }

    private async Task<bool> RobotExists(string robotCode, CancellationToken ct) =>
        await db.Robots.AsNoTracking().AnyAsync(
            item => item.RobotCode == robotCode && item.IsActive, ct);

    private IActionResult Error(NavigationOperation result)
    {
        if (result.Conflict) return Conflict(new { message = result.Error });
        return BadRequest(new { message = result.Error });
    }
}
