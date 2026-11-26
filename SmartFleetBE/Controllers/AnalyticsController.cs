using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController, Route("api/v1/analytics"), Authorize(Roles = AppRoles.AdminOrWarehouseOperator)]
public sealed class AnalyticsController(AnalyticsService service, SmartFleetDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Read(DateOnly? from, DateOnly? to, int? warehouseId, int? robotId, CancellationToken ct)
    {
        try { return Ok(await service.Read(from, to, warehouseId, robotId, ct)); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
    [HttpGet("export/{format}")]
    public async Task<IActionResult> Export(string format, DateOnly? from, DateOnly? to, int? warehouseId, int? robotId, CancellationToken ct)
    {
        if (format is not ("xlsx" or "pdf")) return BadRequest(new { message = "Export format must be xlsx or pdf." });
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try {
            var report = await service.Read(from, to, warehouseId, robotId, ct);
            var bytes = format == "xlsx" ? AnalyticsExport.Xlsx(report) : AnalyticsExport.Pdf(report);
            db.SystemAuditLogs.Add(new() { UserId = actor, ActionCategory = "REPORTING", ActionName = "EXPORT_REPORT", EntityType = "Analytics",
                Description = JsonSerializer.Serialize(new { format, report.Window }), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return File(bytes, format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/pdf",
                $"smartfleet-{report.Window.From:yyyy-MM-dd}-{report.Window.To:yyyy-MM-dd}.{format}");
        } catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
}
