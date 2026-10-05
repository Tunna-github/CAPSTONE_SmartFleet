namespace SmartFleetBE.Model;

// Saved in the same transaction as the assignment. Never roll back an assignment
// after a command might already have reached a physical robot.
public sealed class RobotCommandOutbox
{
    public long Id { get; set; }
    public long AssignmentId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public TaskAssignment Assignment { get; set; } = null!;
}
