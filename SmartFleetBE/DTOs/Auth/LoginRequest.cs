using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.Auth;

public sealed class LoginRequest
{
    [Required]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
