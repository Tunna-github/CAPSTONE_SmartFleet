using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;

namespace SmartFleetBE.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly SmartFleetDbContext _dbContext;

    public UserRepository(SmartFleetDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> GetByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<User>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.UserId)
            .ToListAsync(cancellationToken);
    }
}
