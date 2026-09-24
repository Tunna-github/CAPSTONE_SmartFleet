using SmartFleetBE.DTOs.Users;

namespace SmartFleetBE.Services.Interfaces;

public interface IUserService
{
    Task<UserResponse?> GetByIdAsync(int userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<UserResponse>> GetAllAsync(CancellationToken cancellationToken = default);
}
