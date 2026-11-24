using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Services;

public static class CurrentAccountValidator
{
    // A valid signature alone must not keep a deactivated account or revoked role alive.
    public static async Task<bool> IsCurrentAsync(ClaimsPrincipal? principal,
        SmartFleetDbContext db, CancellationToken ct = default)
    {
        if (!int.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        var user = await db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);
        if (user == null || user.UserRoles.Count == 0) return false;
        var roles = user.UserRoles.Select(ur => ur.Role.RoleName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return roles.SetEquals(principal!.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }
}
