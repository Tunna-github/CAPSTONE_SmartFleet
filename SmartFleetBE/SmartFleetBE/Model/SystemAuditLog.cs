using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class SystemAuditLog
{
    public long AuditId { get; set; }

    public int? UserId { get; set; }

    public string ActionCategory { get; set; } = null!;

    public string ActionName { get; set; } = null!;

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string Description { get; set; } = null!;

    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User? User { get; set; }
}
