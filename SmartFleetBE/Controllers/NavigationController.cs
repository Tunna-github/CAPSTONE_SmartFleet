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
    IConfiguration configuration,
    IHostEnvironment environment,
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

    // Lightweight count endpoint for dashboards, with breakdown by last scanned QR.
    [HttpGet("intersections/{arucoId:int}/approaching")]
    public IActionResult GetApproachingCount(int arucoId)
    {
        var x = coordinator.GetIntersection(arucoId);
        if (x == null) return NotFound(new { message = "Unknown intersection." });
        return Ok(new
        {
            intersectionArUcoId = arucoId,
            x.ApproachingCount,
            x.WaitingCount,
            x.InsideCount,
            x.TotalRelevantCount,
            x.ActiveRobotCode,
            x.ApproachingBySourceArUco,
            x.ApproachingRobots
        });
    }

    // Manual testing with a JWT. A real robot sends the same JSON via MQTT /scan.
    // ?simulate=true skips command publishing ONLY for a Development Admin demo.
    [HttpPost("robots/{robotCode}/scan")]
    public async Task<IActionResult> ReportRobotScan(
        string robotCode, [FromBody] RobotArUcoScanReport report,
        [FromQuery] bool simulate, CancellationToken ct)
    {
        if (!simulate && !configuration.GetValue<bool>("Navigation:Enabled"))
            return StatusCode(503, new { message = "Navigation disabled. Enable only after robot-side NAV_CONTROL is installed." });
        if (simulate && (!environment.IsDevelopment() || !User.IsInRole(AppRoles.Admin)))
            return Forbid();
        var robot = await db.Robots.AsNoTracking()
            .Where(item => item.RobotCode == robotCode && item.IsActive)
            .Select(item => new { item.RobotId, item.RobotCode })
            .SingleOrDefaultAsync(ct);
        if (robot == null)
            return NotFound(new { message = "Unknown or inactive robot code." });
        var preview = simulate ? coordinator.PreviewScan(robot.RobotId, robot.RobotCode, report) : default;
        var result = simulate ? preview.Result : coordinator.OnScan(robot.RobotId, robot.RobotCode, report);
        if (!result.Success) return Error(result);
        if (!simulate)
        {
            try
            {
                await NavigationMqttDispatch.PublishAsync(mqtt, result.Directives, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scan recorded, but MQTT directive delivery failed.");
                return StatusCode(503, new { message = "Scan recorded, but MQTT publish failed." });
            }
        }
        var intersection = simulate ? preview.Intersection : report.NextArUcoId.HasValue
            ? coordinator.GetIntersection(report.NextArUcoId.Value) : null;
        return Ok(new
        {
            robot.RobotCode,
            report.CurrentArUcoId,
            report.NextArUcoId,
            isNextIntersection = intersection != null,
            simulated = simulate,
            intersection
        });
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
        if (!configuration.GetValue<bool>("Navigation:Enabled"))
            return Ok(new { robotCode, config = coordinator.GetRobotConfig(robotCode), navigationEnabled = false });
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
        if (result.Unavailable) return StatusCode(503, new { message = result.Error });
        if (result.Conflict) return Conflict(new { message = result.Error });
        return BadRequest(new { message = result.Error });
    }
}
