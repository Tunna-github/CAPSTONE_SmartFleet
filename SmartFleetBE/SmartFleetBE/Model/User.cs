using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class User
{
    public int UserId { get; set; }

    public string Username { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public string? PhoneNumber { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<DeliveryVerification> DeliveryVerifications { get; set; } = new List<DeliveryVerification>();

    public virtual ICollection<MissionStatusHistory> MissionStatusHistories { get; set; } = new List<MissionStatusHistory>();

    public virtual ICollection<NotificationRecipient> NotificationRecipients { get; set; } = new List<NotificationRecipient>();

    public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public virtual ICollection<RobotIncident> RobotIncidents { get; set; } = new List<RobotIncident>();

    public virtual ICollection<RobotMaintenanceRecord> RobotMaintenanceRecordCompletedByNavigations { get; set; } = new List<RobotMaintenanceRecord>();

    public virtual ICollection<RobotMaintenanceRecord> RobotMaintenanceRecordCreatedByNavigations { get; set; } = new List<RobotMaintenanceRecord>();

    public virtual ICollection<SystemAuditLog> SystemAuditLogs { get; set; } = new List<SystemAuditLog>();

    public virtual ICollection<TaskAssignment> TaskAssignments { get; set; } = new List<TaskAssignment>();

    public virtual ICollection<TaskStatusHistory> TaskStatusHistories { get; set; } = new List<TaskStatusHistory>();

    public virtual ICollection<TransportTask> TransportTasks { get; set; } = new List<TransportTask>();

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
