using SmartFleetBE.Model;

namespace SmartFleetBE.Services.Interfaces;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user, IReadOnlyCollection<string> roles);
}
