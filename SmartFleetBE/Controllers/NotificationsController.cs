using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Controllers;

[ApiController, Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController(SmartFleetDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(await db.NotificationRecipients.AsNoTracking().Where(r => r.UserId == userId)
            .OrderByDescending(r => r.Notification.CreatedAt).Take(100)
            .Select(r => new { r.NotificationId, r.IsRead, r.ReadAt, r.Notification.Title,
                r.Notification.Message, r.Notification.Severity, r.Notification.CreatedAt }).ToListAsync(ct));
    }

    [HttpPost("{notificationId:long}/read")]
    public async Task<IActionResult> MarkRead(long notificationId, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var recipient = await db.NotificationRecipients.SingleOrDefaultAsync(r =>
            r.UserId == userId && r.NotificationId == notificationId, ct);
        if (recipient == null) return NotFound();
        if (!recipient.IsRead) { recipient.IsRead = true; recipient.ReadAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        return NoContent();
    }
}
