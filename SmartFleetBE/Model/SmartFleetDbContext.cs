using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

using SmartFleetBE.Model;
namespace SmartFleetBE;

public partial class SmartFleetDbContext : DbContext
{
    public SmartFleetDbContext()
    {
    }

    public SmartFleetDbContext(DbContextOptions<SmartFleetDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<DeliveryVerification> DeliveryVerifications { get; set; }

    public virtual DbSet<MapConfiguration> MapConfigurations { get; set; }

    public virtual DbSet<Mission> Missions { get; set; }

    public virtual DbSet<MissionStatusHistory> MissionStatusHistories { get; set; }

    public virtual DbSet<MissionWaypoint> MissionWaypoints { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<NotificationRecipient> NotificationRecipients { get; set; }

    public virtual DbSet<RefreshToken> RefreshTokens { get; set; }

    public virtual DbSet<Robot> Robots { get; set; }

    public virtual DbSet<RobotIncident> RobotIncidents { get; set; }

    public virtual DbSet<RobotMaintenanceRecord> RobotMaintenanceRecords { get; set; }

    public virtual DbSet<RobotStatusHistory> RobotStatusHistories { get; set; }

    public virtual DbSet<RobotTelemetryLog> RobotTelemetryLogs { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<Station> Stations { get; set; }

    public virtual DbSet<SystemAuditLog> SystemAuditLogs { get; set; }

    public virtual DbSet<TaskAssignment> TaskAssignments { get; set; }

    public virtual DbSet<TaskStatusHistory> TaskStatusHistories { get; set; }

    public virtual DbSet<TransportTask> TransportTasks { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserRole> UserRoles { get; set; }

    public virtual DbSet<Warehouse> Warehouses { get; set; }

    public virtual DbSet<Zone> Zones { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlServer("Name=ConnectionStrings:DefaultConnection");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryVerification>(entity =>
        {
            entity.HasKey(e => e.VerificationId).HasName("PK__Delivery__306D4927F910F468");

            entity.HasIndex(e => new { e.MissionId, e.VerificationStage }, "UQ_DeliveryVerifications_Mission_Stage").IsUnique();

            entity.Property(e => e.VerificationId).HasColumnName("VerificationID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ExpectedQrPayload)
                .HasMaxLength(150)
                .IsUnicode(false);
            entity.Property(e => e.MissionId).HasColumnName("MissionID");
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.QrPayloadScanned)
                .HasMaxLength(150)
                .IsUnicode(false);
            entity.Property(e => e.VerificationStage)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.VerificationType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("QRCODE");
            entity.Property(e => e.VerifiedAt).HasPrecision(3);

            entity.HasOne(d => d.Mission).WithMany(p => p.DeliveryVerifications)
                .HasForeignKey(d => d.MissionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DeliveryVerifications_Missions");

            entity.HasOne(d => d.VerifiedByNavigation).WithMany(p => p.DeliveryVerifications)
                .HasForeignKey(d => d.VerifiedBy)
                .HasConstraintName("FK_DeliveryVerifications_Users");
        });

        modelBuilder.Entity<MapConfiguration>(entity =>
        {
            entity.HasKey(e => e.MapId).HasName("PK__MapConfi__3265E2FBA5C273BF");

            entity.HasIndex(e => new { e.WarehouseId, e.MapVersion }, "UQ_MapConfigurations_Warehouse_Version").IsUnique();

            entity.HasIndex(e => e.WarehouseId, "UX_MapConfigurations_OneActivePerWarehouse")
                .IsUnique()
                .HasFilter("([IsActive]=(1))");

            entity.Property(e => e.MapId).HasColumnName("MapID");
            entity.Property(e => e.CoordinateSystem)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("LOCAL_METRIC");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.MapImageUrl)
                .HasMaxLength(500)
                .HasColumnName("MapImageURL");
            entity.Property(e => e.MapName).HasMaxLength(120);
            entity.Property(e => e.MapSource)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("LIDAR_SLAM");
            entity.Property(e => e.MapVersion)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.OriginX).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.OriginY).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.OriginYaw).HasColumnType("decimal(10, 6)");
            entity.Property(e => e.ResolutionMeterPerPixel)
                .HasDefaultValue(0.050000m)
                .HasColumnType("decimal(10, 6)");
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");

            entity.HasOne(d => d.Warehouse).WithOne(p => p.MapConfiguration)
                .HasForeignKey<MapConfiguration>(d => d.WarehouseId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MapConfigurations_Warehouses");
        });

        modelBuilder.Entity<Mission>(entity =>
        {
            entity.HasKey(e => e.MissionId).HasName("PK__Missions__66DFB85463EEA7CC");

            entity.HasIndex(e => new { e.MissionStatus, e.LastProgressAt }, "IX_Missions_Status_Progress");

            entity.HasIndex(e => e.AssignmentId, "UQ__Missions__32499E56B445480D").IsUnique();

            entity.HasIndex(e => e.MissionCode, "UQ__Missions__B986D1CA64921B15").IsUnique();

            entity.Property(e => e.MissionId).HasColumnName("MissionID");
            entity.Property(e => e.AssignmentId).HasColumnName("AssignmentID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.EndExecutionTime).HasPrecision(3);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.LastProgressAt).HasPrecision(3);
            entity.Property(e => e.MissionCode)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.MissionStatus)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasDefaultValue("INITIALIZING");
            entity.Property(e => e.StartExecutionTime).HasPrecision(3);
            entity.Property(e => e.TotalDistanceMeters).HasColumnType("decimal(12, 2)");

            entity.HasOne(d => d.Assignment).WithOne(p => p.Mission)
                .HasForeignKey<Mission>(d => d.AssignmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Missions_TaskAssignments");
        });

        modelBuilder.Entity<MissionStatusHistory>(entity =>
        {
            entity.HasKey(e => e.MissionStatusHistoryId).HasName("PK__MissionS__7736D18832D593F8");

            entity.HasIndex(e => new { e.MissionId, e.ChangedAt }, "IX_MissionStatusHistories_Mission_Time").IsDescending(false, true);

            entity.Property(e => e.MissionStatusHistoryId).HasColumnName("MissionStatusHistoryID");
            entity.Property(e => e.ChangedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.MissionId).HasColumnName("MissionID");
            entity.Property(e => e.NewStatus)
                .HasMaxLength(40)
                .IsUnicode(false);
            entity.Property(e => e.PreviousStatus)
                .HasMaxLength(40)
                .IsUnicode(false);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("SYSTEM");

            entity.HasOne(d => d.ChangedByNavigation).WithMany(p => p.MissionStatusHistories)
                .HasForeignKey(d => d.ChangedBy)
                .HasConstraintName("FK_MissionStatusHistories_Users");

            entity.HasOne(d => d.Mission).WithMany(p => p.MissionStatusHistories)
                .HasForeignKey(d => d.MissionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MissionStatusHistories_Missions");
        });

        modelBuilder.Entity<MissionWaypoint>(entity =>
        {
            entity.HasKey(e => e.WaypointId).HasName("PK__MissionW__DF81B324B1BD14F2");

            entity.HasIndex(e => new { e.MissionId, e.SequenceNumber }, "IX_MissionWaypoints_Mission_Sequence");

            entity.HasIndex(e => new { e.MissionId, e.SequenceNumber }, "UQ_MissionWaypoints_Mission_Sequence").IsUnique();

            entity.Property(e => e.WaypointId).HasColumnName("WaypointID");
            entity.Property(e => e.CoordX).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.CoordY).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.MissionId).HasColumnName("MissionID");
            entity.Property(e => e.ReachedAt).HasPrecision(3);
            entity.Property(e => e.StationId).HasColumnName("StationID");
            entity.Property(e => e.WaypointAction)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("PASS_THROUGH");
            entity.Property(e => e.YawAngle).HasColumnType("decimal(10, 6)");

            entity.HasOne(d => d.Mission).WithMany(p => p.MissionWaypoints)
                .HasForeignKey(d => d.MissionId)
                .HasConstraintName("FK_MissionWaypoints_Missions");

            entity.HasOne(d => d.Station).WithMany(p => p.MissionWaypoints)
                .HasForeignKey(d => d.StationId)
                .HasConstraintName("FK_MissionWaypoints_Stations");
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.NotificationId).HasName("PK__Notifica__20CF2E321F842579");

            entity.Property(e => e.NotificationId).HasColumnName("NotificationID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.NotificationCategory)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.RelatedIncidentId).HasColumnName("RelatedIncidentID");
            entity.Property(e => e.RelatedMissionId).HasColumnName("RelatedMissionID");
            entity.Property(e => e.RelatedRobotId).HasColumnName("RelatedRobotID");
            entity.Property(e => e.RelatedTaskId).HasColumnName("RelatedTaskID");
            entity.Property(e => e.Severity)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("INFO");
            entity.Property(e => e.Title).HasMaxLength(180);

            entity.HasOne(d => d.RelatedIncident).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.RelatedIncidentId)
                .HasConstraintName("FK_Notifications_Incidents");

            entity.HasOne(d => d.RelatedMission).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.RelatedMissionId)
                .HasConstraintName("FK_Notifications_Missions");

            entity.HasOne(d => d.RelatedRobot).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.RelatedRobotId)
                .HasConstraintName("FK_Notifications_Robots");

            entity.HasOne(d => d.RelatedTask).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.RelatedTaskId)
                .HasConstraintName("FK_Notifications_Tasks");
        });

        modelBuilder.Entity<NotificationRecipient>(entity =>
        {
            entity.HasKey(e => new { e.NotificationId, e.UserId });

            entity.HasIndex(e => new { e.UserId, e.IsRead }, "IX_NotificationRecipients_User_Read");

            entity.Property(e => e.NotificationId).HasColumnName("NotificationID");
            entity.Property(e => e.UserId).HasColumnName("UserID");
            entity.Property(e => e.ReadAt).HasPrecision(3);

            entity.HasOne(d => d.Notification).WithMany(p => p.NotificationRecipients)
                .HasForeignKey(d => d.NotificationId)
                .HasConstraintName("FK_NotificationRecipients_Notifications");

            entity.HasOne(d => d.User).WithMany(p => p.NotificationRecipients)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_NotificationRecipients_Users");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.RefreshTokenId).HasName("PK__RefreshT__F5845E5950D3F69E");

            entity.HasIndex(e => e.TokenHash, "UQ__RefreshT__BCB33F92D92D7453").IsUnique();

            entity.Property(e => e.RefreshTokenId).HasColumnName("RefreshTokenID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ExpiresAt).HasPrecision(3);
            entity.Property(e => e.RevokedAt).HasPrecision(3);
            entity.Property(e => e.TokenHash).HasMaxLength(255);
            entity.Property(e => e.UserId).HasColumnName("UserID");

            entity.HasOne(d => d.User).WithMany(p => p.RefreshTokens)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("FK_RefreshTokens_Users");
        });

        modelBuilder.Entity<Robot>(entity =>
        {
            entity.HasKey(e => e.RobotId).HasName("PK__Robots__FBB332A1B584813A");

            entity.HasIndex(e => new { e.WarehouseId, e.ConnectionStatus, e.OperationalStatus, e.BatteryPercent }, "IX_Robots_DispatchCandidate");

            entity.HasIndex(e => e.MqttClientIdentifier, "UQ__Robots__558F275E0A2C002B").IsUnique();

            entity.HasIndex(e => e.RobotCode, "UQ__Robots__F9DB9D4E7380E1A3").IsUnique();

            entity.Property(e => e.RobotId).HasColumnName("RobotID");
            entity.Property(e => e.BatteryPercent).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.BatteryVoltage).HasColumnType("decimal(6, 3)");
            entity.Property(e => e.ConnectionStatus)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("OFFLINE");
            entity.Property(e => e.ControlSoftware)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasDefaultValue("NVIDIA_JETRACER_PYTHON");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CurrentCoordX).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.CurrentCoordY).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.CurrentHeading).HasColumnType("decimal(10, 6)");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LastHeartbeatTime).HasPrecision(3);
            entity.Property(e => e.LastTelemetryTime).HasPrecision(3);
            entity.Property(e => e.MacAddress)
                .HasMaxLength(17)
                .IsUnicode(false);
            entity.Property(e => e.Model)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasDefaultValue("JetRacer");
            entity.Property(e => e.MqttClientIdentifier)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.OperationalStatus)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("AVAILABLE");
            entity.Property(e => e.RobotAgentVersion)
                .HasMaxLength(40)
                .IsUnicode(false);
            entity.Property(e => e.RobotCode)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.RobotType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("PHYSICAL");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.Robots)
                .HasForeignKey(d => d.WarehouseId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Robots_Warehouses");
        });

        modelBuilder.Entity<RobotIncident>(entity =>
        {
            entity.HasKey(e => e.IncidentId).HasName("PK__RobotInc__3D8053925C3BBF17");

            entity.HasIndex(e => new { e.IsResolved, e.Severity, e.CreatedAt }, "IX_RobotIncidents_Open").IsDescending(false, false, true);

            entity.Property(e => e.IncidentId).HasColumnName("IncidentID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.IncidentType)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.MissionId).HasColumnName("MissionID");
            entity.Property(e => e.ResolutionNote).HasMaxLength(1000);
            entity.Property(e => e.ResolvedAt).HasPrecision(3);
            entity.Property(e => e.RobotId).HasColumnName("RobotID");
            entity.Property(e => e.Severity)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("WARNING");
            entity.Property(e => e.TaskId).HasColumnName("TaskID");

            entity.HasOne(d => d.Mission).WithMany(p => p.RobotIncidents)
                .HasForeignKey(d => d.MissionId)
                .HasConstraintName("FK_RobotIncidents_Missions");

            entity.HasOne(d => d.ResolvedByNavigation).WithMany(p => p.RobotIncidents)
                .HasForeignKey(d => d.ResolvedBy)
                .HasConstraintName("FK_RobotIncidents_ResolvedBy");

            entity.HasOne(d => d.Robot).WithMany(p => p.RobotIncidents)
                .HasForeignKey(d => d.RobotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RobotIncidents_Robots");

            entity.HasOne(d => d.Task).WithMany(p => p.RobotIncidents)
                .HasForeignKey(d => d.TaskId)
                .HasConstraintName("FK_RobotIncidents_Tasks");
        });

        modelBuilder.Entity<RobotMaintenanceRecord>(entity =>
        {
            entity.HasKey(e => e.MaintenanceId).HasName("PK__RobotMai__E60542B564C218A4");

            entity.HasIndex(e => new { e.RobotId, e.MaintenanceStatus, e.CreatedAt }, "IX_RobotMaintenance_Robot_Status").IsDescending(false, false, true);

            entity.Property(e => e.MaintenanceId).HasColumnName("MaintenanceID");
            entity.Property(e => e.CompletedAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.MaintenanceStatus)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("SCHEDULED");
            entity.Property(e => e.MaintenanceType)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.RobotId).HasColumnName("RobotID");
            entity.Property(e => e.ScheduledAt).HasPrecision(3);
            entity.Property(e => e.StartedAt).HasPrecision(3);

            entity.HasOne(d => d.CompletedByNavigation).WithMany(p => p.RobotMaintenanceRecordCompletedByNavigations)
                .HasForeignKey(d => d.CompletedBy)
                .HasConstraintName("FK_RobotMaintenanceRecords_CompletedBy");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.RobotMaintenanceRecordCreatedByNavigations)
                .HasForeignKey(d => d.CreatedBy)
                .HasConstraintName("FK_RobotMaintenanceRecords_CreatedBy");

            entity.HasOne(d => d.Robot).WithMany(p => p.RobotMaintenanceRecords)
                .HasForeignKey(d => d.RobotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RobotMaintenanceRecords_Robots");
        });

        modelBuilder.Entity<RobotStatusHistory>(entity =>
        {
            entity.HasKey(e => e.RobotStatusHistoryId).HasName("PK__RobotSta__DE50B2D89DEB2C77");

            entity.HasIndex(e => new { e.RobotId, e.ChangedAt }, "IX_RobotStatusHistories_Robot_Time").IsDescending(false, true);

            entity.Property(e => e.RobotStatusHistoryId).HasColumnName("RobotStatusHistoryID");
            entity.Property(e => e.ChangedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.NewConnectionStatus)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.NewOperationalStatus)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.PreviousConnectionStatus)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.PreviousOperationalStatus)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.RobotId).HasColumnName("RobotID");

            entity.HasOne(d => d.Robot).WithMany(p => p.RobotStatusHistories)
                .HasForeignKey(d => d.RobotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RobotStatusHistories_Robots");
        });

        modelBuilder.Entity<RobotTelemetryLog>(entity =>
        {
            entity.HasKey(e => e.TelemetryId).HasName("PK__RobotTel__157CAF178D087245");

            entity.HasIndex(e => new { e.RobotId, e.RecordedAt }, "IX_RobotTelemetry_Robot_Time").IsDescending(false, true);

            entity.Property(e => e.TelemetryId).HasColumnName("TelemetryID");
            entity.Property(e => e.AngularVelocity).HasColumnType("decimal(8, 3)");
            entity.Property(e => e.BatteryCurrentMa).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.BatteryPercentage).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.BatteryPowerMw).HasColumnType("decimal(12, 2)");
            entity.Property(e => e.BatteryVoltage).HasColumnType("decimal(6, 3)");
            entity.Property(e => e.CpuTemperature).HasColumnType("decimal(6, 2)");
            entity.Property(e => e.Heading).HasColumnType("decimal(10, 6)");
            entity.Property(e => e.LinearVelocity).HasColumnType("decimal(8, 3)");
            entity.Property(e => e.PositionX).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.PositionY).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.RecordedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.RobotId).HasColumnName("RobotID");
            entity.Property(e => e.WifiSignalDbm).HasColumnType("decimal(6, 2)");

            entity.HasOne(d => d.Robot).WithMany(p => p.RobotTelemetryLogs)
                .HasForeignKey(d => d.RobotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RobotTelemetryLogs_Robots");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("PK__Roles__8AFACE3A6F72D3EF");

            entity.HasIndex(e => e.RoleName, "UQ__Roles__8A2B6160F78CB88D").IsUnique();

            entity.Property(e => e.RoleId).HasColumnName("RoleID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.RoleName).HasMaxLength(50);
        });

        modelBuilder.Entity<Station>(entity =>
        {
            entity.HasKey(e => e.StationId).HasName("PK__Stations__E0D8A6DD087AA7E7");

            entity.HasIndex(e => e.StationCode, "UQ__Stations__D3885618FF7AF6E8").IsUnique();

            entity.HasIndex(e => e.QrCodePayload, "UX_Stations_QrCodePayload")
                .IsUnique()
                .HasFilter("([QrCodePayload] IS NOT NULL)");

            entity.Property(e => e.StationId).HasColumnName("StationID");
            entity.Property(e => e.CoordX).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.CoordY).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.CoordZ).HasColumnType("decimal(12, 4)");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.QrCodePayload)
                .HasMaxLength(150)
                .IsUnicode(false);
            entity.Property(e => e.StationCode)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.StationName).HasMaxLength(120);
            entity.Property(e => e.StationType)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.YawAngle).HasColumnType("decimal(10, 6)");
            entity.Property(e => e.ZoneId).HasColumnName("ZoneID");

            entity.HasOne(d => d.Zone).WithMany(p => p.Stations)
                .HasForeignKey(d => d.ZoneId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Stations_Zones");
        });

        modelBuilder.Entity<SystemAuditLog>(entity =>
        {
            entity.HasKey(e => e.AuditId).HasName("PK__SystemAu__A17F23B8AA02DA70");

            entity.Property(e => e.AuditId).HasColumnName("AuditID");
            entity.Property(e => e.ActionCategory)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.ActionName)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.EntityId)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("EntityID");
            entity.Property(e => e.EntityType)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false);
            entity.Property(e => e.UserId).HasColumnName("UserID");

            entity.HasOne(d => d.User).WithMany(p => p.SystemAuditLogs)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("FK_SystemAuditLogs_Users");
        });

        modelBuilder.Entity<TaskAssignment>(entity =>
        {
            entity.HasKey(e => e.AssignmentId).HasName("PK__TaskAssi__32499E579FA23A81");

            entity.HasIndex(e => new { e.RobotId, e.AssignmentStatus, e.AssignedAt }, "IX_TaskAssignments_Robot_Status").IsDescending(false, false, true);

            entity.HasIndex(e => e.RobotId, "UX_TaskAssignments_OneActivePerRobot")
                .IsUnique()
                .HasFilter("([AssignmentStatus]='ACTIVE')");

            entity.HasIndex(e => e.TaskId, "UX_TaskAssignments_OneActivePerTask")
                .IsUnique()
                .HasFilter("([AssignmentStatus]='ACTIVE')");

            entity.Property(e => e.AssignmentId).HasColumnName("AssignmentID");
            entity.Property(e => e.AssignedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.AssignmentReason).HasMaxLength(500);
            entity.Property(e => e.AssignmentStatus)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("ACTIVE");
            entity.Property(e => e.AssignmentType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("AUTO");
            entity.Property(e => e.BatteryPercentAtAssignment).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.CandidateDistanceMeters).HasColumnType("decimal(12, 2)");
            entity.Property(e => e.DispatchScore).HasColumnType("decimal(14, 4)");
            entity.Property(e => e.EndedAt).HasPrecision(3);
            entity.Property(e => e.ReassignmentReason).HasMaxLength(500);
            entity.Property(e => e.RobotId).HasColumnName("RobotID");
            entity.Property(e => e.TaskId).HasColumnName("TaskID");

            entity.HasOne(d => d.AssignedByNavigation).WithMany(p => p.TaskAssignments)
                .HasForeignKey(d => d.AssignedBy)
                .HasConstraintName("FK_TaskAssignments_Users");

            entity.HasOne(d => d.Robot).WithOne(p => p.TaskAssignment)
                .HasForeignKey<TaskAssignment>(d => d.RobotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TaskAssignments_Robots");

            entity.HasOne(d => d.Task).WithOne(p => p.TaskAssignment)
                .HasForeignKey<TaskAssignment>(d => d.TaskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TaskAssignments_Tasks");
        });

        modelBuilder.Entity<TaskStatusHistory>(entity =>
        {
            entity.HasKey(e => e.TaskStatusHistoryId).HasName("PK__TaskStat__AAC202A6D1D8F8AC");

            entity.HasIndex(e => new { e.TaskId, e.ChangedAt }, "IX_TaskStatusHistories_Task_Time").IsDescending(false, true);

            entity.Property(e => e.TaskStatusHistoryId).HasColumnName("TaskStatusHistoryID");
            entity.Property(e => e.ChangedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.NewStatus)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.PreviousStatus)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.TaskId).HasColumnName("TaskID");

            entity.HasOne(d => d.ChangedByNavigation).WithMany(p => p.TaskStatusHistories)
                .HasForeignKey(d => d.ChangedBy)
                .HasConstraintName("FK_TaskStatusHistories_Users");

            entity.HasOne(d => d.Task).WithMany(p => p.TaskStatusHistories)
                .HasForeignKey(d => d.TaskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TaskStatusHistories_Tasks");
        });

        modelBuilder.Entity<TransportTask>(entity =>
        {
            entity.HasKey(e => e.TaskId).HasName("PK__Transpor__7C6949D1AC49FF2D");

            entity.HasIndex(e => new { e.TaskStatus, e.PriorityLevel, e.CreatedAt }, "IX_TransportTasks_Status_Priority").IsDescending(false, true, false);

            entity.HasIndex(e => e.TaskTrackingCode, "UQ__Transpor__3AE3883397055726").IsUnique();

            entity.Property(e => e.TaskId).HasColumnName("TaskID");
            entity.Property(e => e.AssignedAt).HasPrecision(3);
            entity.Property(e => e.CancellationReason).HasMaxLength(500);
            entity.Property(e => e.CancelledAt).HasPrecision(3);
            entity.Property(e => e.CompletedAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.DeliveryStationId).HasColumnName("DeliveryStationID");
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.ItemDescription).HasMaxLength(500);
            entity.Property(e => e.PackageCode)
                .HasMaxLength(80)
                .IsUnicode(false);
            entity.Property(e => e.PayloadWeightKg).HasColumnType("decimal(8, 2)");
            entity.Property(e => e.PickupStationId).HasColumnName("PickupStationID");
            entity.Property(e => e.PriorityLevel).HasDefaultValue(2);
            entity.Property(e => e.QueuedAt).HasPrecision(3);
            entity.Property(e => e.ScheduledTime).HasPrecision(3);
            entity.Property(e => e.StartedAt).HasPrecision(3);
            entity.Property(e => e.TaskStatus)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.TaskTrackingCode)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.TransportTasks)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TransportTasks_CreatedBy");

            entity.HasOne(d => d.DeliveryStation).WithMany(p => p.TransportTaskDeliveryStations)
                .HasForeignKey(d => d.DeliveryStationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TransportTasks_DeliveryStation");

            entity.HasOne(d => d.PickupStation).WithMany(p => p.TransportTaskPickupStations)
                .HasForeignKey(d => d.PickupStationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TransportTasks_PickupStation");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.TransportTasks)
                .HasForeignKey(d => d.WarehouseId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_TransportTasks_Warehouses");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("PK__Users__1788CCACCEA03989");

            entity.HasIndex(e => e.Username, "UQ__Users__536C85E44BB02AEF").IsUnique();

            entity.HasIndex(e => e.Email, "UQ__Users__A9D105342ED34DCC").IsUnique();

            entity.Property(e => e.UserId).HasColumnName("UserID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Email)
                .HasMaxLength(150)
                .IsUnicode(false);
            entity.Property(e => e.FullName).HasMaxLength(120);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.PasswordHash).HasMaxLength(255);
            entity.Property(e => e.PhoneNumber)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.Username)
                .HasMaxLength(50)
                .IsUnicode(false);
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.RoleId });

            entity.Property(e => e.UserId).HasColumnName("UserID");
            entity.Property(e => e.RoleId).HasColumnName("RoleID");
            entity.Property(e => e.AssignedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.Role).WithMany(p => p.UserRoles)
                .HasForeignKey(d => d.RoleId)
                .HasConstraintName("FK_UserRoles_Roles");

            entity.HasOne(d => d.User).WithMany(p => p.UserRoles)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("FK_UserRoles_Users");
        });

        modelBuilder.Entity<Warehouse>(entity =>
        {
            entity.HasKey(e => e.WarehouseId).HasName("PK__Warehous__2608AFD923AF8508");

            entity.HasIndex(e => e.WarehouseCode, "UQ__Warehous__1686A0569815041D").IsUnique();

            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");
            entity.Property(e => e.Address).HasMaxLength(255);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.WarehouseCode)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.WarehouseName).HasMaxLength(120);
        });

        modelBuilder.Entity<Zone>(entity =>
        {
            entity.HasKey(e => e.ZoneId).HasName("PK__Zones__601667952B02B43F");

            entity.HasIndex(e => new { e.WarehouseId, e.ZoneCode }, "UQ_Zones_Warehouse_Code").IsUnique();

            entity.HasIndex(e => new { e.WarehouseId, e.ZoneName }, "UQ_Zones_Warehouse_Name").IsUnique();

            entity.Property(e => e.ZoneId).HasColumnName("ZoneID");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.SpeedLimit)
                .HasDefaultValue(0.50m)
                .HasColumnType("decimal(5, 2)");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");
            entity.Property(e => e.ZoneCode)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.ZoneName).HasMaxLength(80);
            entity.Property(e => e.ZoneType)
                .HasMaxLength(30)
                .IsUnicode(false);

            entity.HasOne(d => d.Warehouse).WithMany(p => p.Zones)
                .HasForeignKey(d => d.WarehouseId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Zones_Warehouses");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
