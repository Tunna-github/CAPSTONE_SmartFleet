using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class RobotIncident
{
    public long IncidentId { get; set; }

    public int RobotId { get; set; }

    public long? TaskId { get; set; }

    public long? MissionId { get; set; }

    public string IncidentType { get; set; } = null!;

    public string Severity { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsResolved { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int? ResolvedBy { get; set; }

    public string? ResolutionNote { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Mission? Mission { get; set; }

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual User? ResolvedByNavigation { get; set; }

    public virtual Robot Robot { get; set; } = null!;

    public virtual TransportTask? Task { get; set; }
}
