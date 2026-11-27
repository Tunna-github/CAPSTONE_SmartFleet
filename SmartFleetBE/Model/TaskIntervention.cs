namespace SmartFleetBE.Model;

// Durable safety overlay: task/mission statuses remain compatible with existing DB constraints.
public sealed class TaskIntervention
{
    public long TaskId { get; set; }
    public string State { get; set; } = "NORMAL";
    public bool Paused { get; set; }
    public string? CommandId { get; set; }
    public int Generation { get; set; }
    public bool CommandAcknowledged { get; set; }
    public string? PauseContextJson { get; set; }
    public string? ResumeFailureReason { get; set; }
    public DateTime? ResumedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
