using SmartFleetBE.Model;

namespace SmartFleetBE.Repositories.Interfaces;

public interface IAuthRepository
{
    Task<User?> GetUserForLoginAsync(string usernameOrEmail, CancellationToken cancellationToken = default);
}
