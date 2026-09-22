using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/access-test")]
[Authorize]
public sealed class AccessTestController : ControllerBase
{
    [HttpGet("authenticated")]
    public IActionResult AuthenticatedOnly()
        => Ok(new { message = "JWT is valid." });

    [HttpGet("admin")]
    [Authorize(Roles = AppRoles.Admin)]
    public IActionResult AdminOnly()
        => Ok(new { message = "ADMIN access granted." });

    [HttpGet("operator")]
    [Authorize(Roles = AppRoles.WarehouseOperator)]
    public IActionResult OperatorOnly()
        => Ok(new { message = "WAREHOUSE_OPERATOR access granted." });

    [HttpGet("maintenance")]
    [Authorize(Roles = AppRoles.MaintenanceTechnician)]
    public IActionResult MaintenanceOnly()
        => Ok(new { message = "MAINTENANCE_TECHNICIAN access granted." });
}
