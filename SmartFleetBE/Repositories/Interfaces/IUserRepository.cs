using SmartFleetBE.Model;

namespace SmartFleetBE.Repositories.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(
        int userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<User>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<User?> GetTrackedByIdAsync(
        int userId,
        CancellationToken cancellationToken = default);

    Task<bool> UsernameExistsAsync(
        string username,
        int? excludeUserId = null,
        CancellationToken cancellationToken = default);

    Task<bool> EmailExistsAsync(
        string email,
        int? excludeUserId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Role>> GetRolesByNamesAsync(
        IEnumerable<string> roleNames,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        User user,
        CancellationToken cancellationToken = default);

    void RemoveUserRoles(IEnumerable<UserRole> userRoles);

    Task SaveChangesAsync(
        CancellationToken cancellationToken = default);
}