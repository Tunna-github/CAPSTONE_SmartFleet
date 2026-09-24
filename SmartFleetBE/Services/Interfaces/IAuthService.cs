using SmartFleetBE.DTOs.Auth;

namespace SmartFleetBE.Services.Interfaces;

public interface IAuthService
{
    Task<SessionResponse?> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default);
}
