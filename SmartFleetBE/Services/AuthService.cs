using SmartFleetBE.DTOs.Auth;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public sealed class AuthService : IAuthService
{
    private readonly IAuthRepository _authRepository;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthService(IAuthRepository authRepository, IJwtTokenService jwtTokenService)
    {
        _authRepository = authRepository;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<SessionResponse?> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await _authRepository.GetUserForAuthenticationAsync(
            request.UsernameOrEmail,
            cancellationToken);

        if (user is null || !user.IsActive)
        {
            return null;
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return null;
        }

        var roles = user.UserRoles
            .Select(ur => ur.Role.RoleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (roles.Length == 0)
        {
            return null;
        }

        var token = _jwtTokenService.CreateAccessToken(user, roles);

        return new SessionResponse
        {
            AccessToken = token.Token,
            ExpiresAtUtc = token.ExpiresAtUtc,
            User = new AuthenticatedUserResponse
            {
                UserId = user.UserId,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                Roles = roles
            }
        };
    }
}
