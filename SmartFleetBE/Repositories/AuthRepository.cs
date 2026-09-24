using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;

namespace SmartFleetBE.Repositories;

public sealed class AuthRepository : IAuthRepository
{
    private readonly SmartFleetDbContext _dbContext;

    public AuthRepository(SmartFleetDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> GetUserForLoginAsync(
        string usernameOrEmail,
        CancellationToken cancellationToken = default)
    {
        var normalized = usernameOrEmail.Trim();

        return _dbContext.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(
                u => u.Username == normalized || u.Email == normalized,
                cancellationToken);
    }
}
