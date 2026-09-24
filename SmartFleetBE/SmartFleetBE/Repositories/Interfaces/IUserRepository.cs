using SmartFleetBE.Model;

namespace SmartFleetBE.Repositories.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<User>> GetAllAsync(CancellationToken cancellationToken = default);
}
