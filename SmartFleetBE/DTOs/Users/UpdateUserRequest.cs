using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.Users;

public sealed class UpdateUserRequest
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string FullName { get; set; } = string.Empty;

    [StringLength(20)]
    public string? PhoneNumber { get; set; }
}