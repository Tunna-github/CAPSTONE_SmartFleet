using SmartFleetBE.Model;

namespace SmartFleetBE.Repositories.Interfaces;

public interface IAuthRepository
{
    Task<User?> GetUserForAuthenticationAsync(
        string usernameOrEmail,
        CancellationToken cancellationToken = default);
}
