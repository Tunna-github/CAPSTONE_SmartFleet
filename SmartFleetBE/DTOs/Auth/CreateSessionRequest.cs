using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.Auth;

public sealed class CreateSessionRequest
{
    [Required]
    [StringLength(150)]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;
}
