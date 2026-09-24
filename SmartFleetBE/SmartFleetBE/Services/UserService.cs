using SmartFleetBE.DTOs.Users;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public sealed class UserService : IUserService
{
    private readonly IUserRepository _userRepository;

    public UserService(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<UserResponse?> GetByIdAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
        return user is null ? null : Map(user);
    }

    public async Task<IReadOnlyCollection<UserResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var users = await _userRepository.GetAllAsync(cancellationToken);
        return users.Select(Map).ToArray();
    }

    private static UserResponse Map(User user)
    {
        return new UserResponse
        {
            UserId = user.UserId,
            Username = user.Username,
            Email = user.Email,
            FullName = user.FullName,
            PhoneNumber = user.PhoneNumber,
            IsActive = user.IsActive,
            Roles = user.UserRoles
                .Select(ur => ur.Role.RoleName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }
}
