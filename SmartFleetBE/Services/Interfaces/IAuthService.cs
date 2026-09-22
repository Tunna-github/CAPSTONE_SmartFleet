using SmartFleetBE.DTOs.Auth;

namespace SmartFleetBE.Services.Interfaces;

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}
