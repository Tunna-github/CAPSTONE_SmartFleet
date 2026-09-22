using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.Users;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// Returns the currently authenticated user.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return Unauthorized();
        }

        var user = await _userService.GetByIdAsync(userId, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Returns all user accounts. ADMIN only.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(typeof(IReadOnlyCollection<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyCollection<UserResponse>>> GetUsers(
        CancellationToken cancellationToken)
    {
        var users = await _userService.GetAllAsync(cancellationToken);
        return Ok(users);
    }
}
