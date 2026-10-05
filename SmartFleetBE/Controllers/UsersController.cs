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

    public UsersController(
        IUserService userService)
    {
        _userService = userService;
    }

    // =================================================
    // CURRENT USER
    // =================================================

    [HttpGet("me")]
    public async Task<ActionResult<UserResponse>> GetCurrentUser(
        CancellationToken cancellationToken)
    {
        var userIdValue =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (!int.TryParse(
            userIdValue,
            out var userId))
        {
            return Unauthorized();
        }

        var user =
            await _userService.GetByIdAsync(
                userId,
                cancellationToken);

        return user is null
            ? NotFound()
            : Ok(user);
    }


    // =================================================
    // ADMIN - GET ALL USERS
    // =================================================

    [HttpGet]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<
        ActionResult<IReadOnlyCollection<UserResponse>>> GetUsers(
        CancellationToken cancellationToken)
    {
        var users =
            await _userService.GetAllAsync(
                cancellationToken);

        return Ok(users);
    }


    // =================================================
    // ADMIN - GET USER BY ID
    // =================================================

    [HttpGet("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<UserResponse>> GetUser(
        int id,
        CancellationToken cancellationToken)
    {
        var user =
            await _userService.GetByIdAsync(
                id,
                cancellationToken);

        return user is null
            ? NotFound(new
            {
                message = "User not found."
            })
            : Ok(user);
    }


    // =================================================
    // ADMIN - CREATE USER
    // =================================================

    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<UserResponse>> CreateUser(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var user =
                await _userService.CreateAsync(
                    request,
                    cancellationToken);

            return CreatedAtAction(
                nameof(GetUser),
                new
                {
                    id = user.UserId
                },
                user);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new
            {
                message = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }


    // =================================================
    // ADMIN - UPDATE USER
    // =================================================

    [HttpPut("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<UserResponse>> UpdateUser(
        int id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var user =
                await _userService.UpdateAsync(
                    id,
                    request,
                    cancellationToken);

            return user is null
                ? NotFound(new
                {
                    message = "User not found."
                })
                : Ok(user);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new
            {
                message = ex.Message
            });
        }
    }


    // =================================================
    // ADMIN - ACTIVATE USER
    // =================================================

    [HttpPatch("{id:int}/activate")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<UserResponse>> ActivateUser(
        int id,
        CancellationToken cancellationToken)
    {
        var user =
            await _userService.SetActiveAsync(
                id,
                true,
                cancellationToken);

        return user is null
            ? NotFound(new
            {
                message = "User not found."
            })
            : Ok(user);
    }


    // =================================================
    // ADMIN - DEACTIVATE USER
    // =================================================

    [HttpPatch("{id:int}/deactivate")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<UserResponse>> DeactivateUser(
        int id,
        CancellationToken cancellationToken)
    {
        var user =
            await _userService.SetActiveAsync(
                id,
                false,
                cancellationToken);

        return user is null
            ? NotFound(new
            {
                message = "User not found."
            })
            : Ok(user);
    }


    // =================================================
    // ADMIN - ASSIGN ROLES
    // =================================================

    [HttpPut("{id:int}/roles")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<UserResponse>> AssignRoles(
        int id,
        [FromBody] AssignRolesRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var user =
                await _userService.AssignRolesAsync(
                    id,
                    request,
                    cancellationToken);

            return user is null
                ? NotFound(new
                {
                    message = "User not found."
                })
                : Ok(user);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }
}