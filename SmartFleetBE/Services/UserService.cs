using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.Users;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public sealed class UserService : IUserService
{
    private readonly IUserRepository _userRepository;

    private static readonly string[] AllowedRoles =
    {
        AppRoles.Admin,
        AppRoles.WarehouseOperator,
        AppRoles.MaintenanceTechnician
    };

    public UserService(
        IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<UserResponse?> GetByIdAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(
            userId,
            cancellationToken);

        return user is null
            ? null
            : Map(user);
    }

    public async Task<IReadOnlyCollection<UserResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var users = await _userRepository.GetAllAsync(
            cancellationToken);

        return users
            .Select(Map)
            .ToArray();
    }

    public async Task<UserResponse> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = request.Username.Trim();
        var email = request.Email.Trim();

        if (await _userRepository.UsernameExistsAsync(
            username,
            null,
            cancellationToken))
        {
            throw new InvalidOperationException(
                "Username already exists.");
        }

        if (await _userRepository.EmailExistsAsync(
            email,
            null,
            cancellationToken))
        {
            throw new InvalidOperationException(
                "Email already exists.");
        }

        var roles = await ResolveRolesAsync(
            request.Roles,
            cancellationToken);

        var user = new User
        {
            Username = username,
            Email = email,
            PasswordHash =
                BCrypt.Net.BCrypt.HashPassword(
                    request.Password),

            FullName = request.FullName.Trim(),

            PhoneNumber =
                string.IsNullOrWhiteSpace(
                    request.PhoneNumber)
                    ? null
                    : request.PhoneNumber.Trim(),

            IsActive = true
        };

        foreach (var role in roles)
        {
            user.UserRoles.Add(
                new UserRole
                {
                    RoleId = role.RoleId,
                    Role = role
                });
        }

        await _userRepository.AddAsync(
            user,
            cancellationToken);

        await _userRepository.SaveChangesAsync(
            cancellationToken);

        var created =
            await _userRepository.GetByIdAsync(
                user.UserId,
                cancellationToken);

        return Map(created!);
    }

    public async Task<UserResponse?> UpdateAsync(
        int userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var user =
            await _userRepository.GetTrackedByIdAsync(
                userId,
                cancellationToken);

        if (user is null)
        {
            return null;
        }

        var username = request.Username.Trim();
        var email = request.Email.Trim();

        if (await _userRepository.UsernameExistsAsync(
            username,
            userId,
            cancellationToken))
        {
            throw new InvalidOperationException(
                "Username already exists.");
        }

        if (await _userRepository.EmailExistsAsync(
            email,
            userId,
            cancellationToken))
        {
            throw new InvalidOperationException(
                "Email already exists.");
        }

        user.Username = username;
        user.Email = email;
        user.FullName = request.FullName.Trim();

        user.PhoneNumber =
            string.IsNullOrWhiteSpace(
                request.PhoneNumber)
                ? null
                : request.PhoneNumber.Trim();

        user.UpdatedAt = DateTime.UtcNow;

        await _userRepository.SaveChangesAsync(
            cancellationToken);

        return await GetByIdAsync(
            userId,
            cancellationToken);
    }

    public async Task<UserResponse?> SetActiveAsync(
        int userId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var user =
            await _userRepository.GetTrackedByIdAsync(
                userId,
                cancellationToken);

        if (user is null)
        {
            return null;
        }

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;

        await _userRepository.SaveChangesAsync(
            cancellationToken);

        return await GetByIdAsync(
            userId,
            cancellationToken);
    }

    public async Task<UserResponse?> AssignRolesAsync(
        int userId,
        AssignRolesRequest request,
        CancellationToken cancellationToken = default)
    {
        var user =
            await _userRepository.GetTrackedByIdAsync(
                userId,
                cancellationToken);

        if (user is null)
        {
            return null;
        }

        var roles = await ResolveRolesAsync(
            request.Roles,
            cancellationToken);

        _userRepository.RemoveUserRoles(
            user.UserRoles.ToList());

        user.UserRoles.Clear();

        foreach (var role in roles)
        {
            user.UserRoles.Add(
                new UserRole
                {
                    UserId = user.UserId,
                    RoleId = role.RoleId,
                    Role = role
                });
        }

        user.UpdatedAt = DateTime.UtcNow;

        await _userRepository.SaveChangesAsync(
            cancellationToken);

        return await GetByIdAsync(
            userId,
            cancellationToken);
    }

    private async Task<IReadOnlyCollection<Role>> ResolveRolesAsync(
        IEnumerable<string> requestedRoles,
        CancellationToken cancellationToken)
    {
        var names = requestedRoles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            throw new ArgumentException(
                "At least one role is required.");
        }

        var invalidRoles = names
            .Where(name =>
                !AllowedRoles.Contains(
                    name,
                    StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (invalidRoles.Length > 0)
        {
            throw new ArgumentException(
                $"Invalid role(s): {string.Join(", ", invalidRoles)}");
        }

        var roles =
            await _userRepository.GetRolesByNamesAsync(
                names,
                cancellationToken);

        if (roles.Count != names.Length)
        {
            throw new ArgumentException(
                "One or more roles do not exist in the database.");
        }

        return roles;
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
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }
}