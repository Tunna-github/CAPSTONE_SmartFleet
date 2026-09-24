using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class TaskAssignment
{
    public long AssignmentId { get; set; }

    public long TaskId { get; set; }

    public int RobotId { get; set; }

    public string AssignmentType { get; set; } = null!;

    public int? AssignedBy { get; set; }

    public string AssignmentStatus { get; set; } = null!;

    public decimal? DispatchScore { get; set; }

    public decimal? CandidateDistanceMeters { get; set; }

    public decimal? BatteryPercentAtAssignment { get; set; }

    public int? WorkloadAtAssignment { get; set; }

    public string? AssignmentReason { get; set; }

    public string? ReassignmentReason { get; set; }

    public DateTime AssignedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public virtual User? AssignedByNavigation { get; set; }

    public virtual Mission? Mission { get; set; }

    public virtual Robot Robot { get; set; } = null!;

    public virtual TransportTask Task { get; set; } = null!;
}
