using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.Maintenance;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/maintenance-records")]
[Authorize(Roles = AppRoles.AdminOrMaintenanceTechnician)]
public sealed class MaintenanceRecordsController : ControllerBase
{
    private readonly IMaintenanceRecordService _maintenanceRecordService;

    public MaintenanceRecordsController(IMaintenanceRecordService maintenanceRecordService)
    {
        _maintenanceRecordService = maintenanceRecordService;
    }

    /// <summary>
    /// Returns robot maintenance records. ADMIN and MAINTENANCE_TECHNICIAN only.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<MaintenanceRecordResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<MaintenanceRecordResponse>>> GetMaintenanceRecords(
        CancellationToken cancellationToken)
    {
        var records = await _maintenanceRecordService.GetAllAsync(cancellationToken);
        return Ok(records);
    }
}
