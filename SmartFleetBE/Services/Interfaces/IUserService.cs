using SmartFleetBE.DTOs.Users;

namespace SmartFleetBE.Services.Interfaces;

public interface IUserService
{
    Task<UserResponse?> GetByIdAsync(
        int userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<UserResponse>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<UserResponse> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default);

    Task<UserResponse?> UpdateAsync(
        int userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default);

    Task<UserResponse?> SetActiveAsync(
        int userId,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<UserResponse?> AssignRolesAsync(
        int userId,
        AssignRolesRequest request,
        CancellationToken cancellationToken = default);
}