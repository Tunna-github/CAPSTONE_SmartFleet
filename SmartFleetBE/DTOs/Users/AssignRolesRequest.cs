using System.ComponentModel.DataAnnotations;

namespace SmartFleetBE.DTOs.Users;

public sealed class AssignRolesRequest
{
    [Required]
    [MinLength(1)]
    public List<string> Roles { get; set; } = new();
}