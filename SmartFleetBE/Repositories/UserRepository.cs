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

    public Task<User?> GetByIdAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(
                u => u.UserId == userId,
                cancellationToken);
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

    public Task<User?> GetTrackedByIdAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(
                u => u.UserId == userId,
                cancellationToken);
    }

    public Task<bool> UsernameExistsAsync(
        string username,
        int? excludeUserId = null,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users.AnyAsync(
            u =>
                u.Username == username &&
                (!excludeUserId.HasValue ||
                 u.UserId != excludeUserId.Value),
            cancellationToken);
    }

    public Task<bool> EmailExistsAsync(
        string email,
        int? excludeUserId = null,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users.AnyAsync(
            u =>
                u.Email == email &&
                (!excludeUserId.HasValue ||
                 u.UserId != excludeUserId.Value),
            cancellationToken);
    }

    public async Task<IReadOnlyCollection<Role>> GetRolesByNamesAsync(
        IEnumerable<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        var names = roleNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await _dbContext.Roles
            .Where(r => names.Contains(r.RoleName))
            .ToListAsync(cancellationToken);
    }

    public Task AddAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users.AddAsync(
            user,
            cancellationToken)
            .AsTask();
    }

    public void RemoveUserRoles(
        IEnumerable<UserRole> userRoles)
    {
        _dbContext.UserRoles.RemoveRange(userRoles);
    }

    public async Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(
            cancellationToken);
    }
}