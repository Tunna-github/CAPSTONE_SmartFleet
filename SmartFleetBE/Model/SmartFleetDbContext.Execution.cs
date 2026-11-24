using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE;

public partial class SmartFleetDbContext
{
    public DbSet<MissionExecution> MissionExecutions => Set<MissionExecution>();
    public DbSet<VerificationAttempt> VerificationAttempts => Set<VerificationAttempt>();
    public DbSet<RobotQrScanEvent> RobotQrScanEvents => Set<RobotQrScanEvent>();

    private static void ConfigureExecution(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MissionExecution>(e =>
        {
            e.ToTable("MissionExecutions"); e.HasKey(x => x.MissionId);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.RoutePayloadJson).HasColumnType("text");
            e.HasOne(x => x.Mission).WithOne().HasForeignKey<MissionExecution>(x => x.MissionId);
        });
        modelBuilder.Entity<VerificationAttempt>(e =>
        {
            e.ToTable("VerificationAttempts"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.MissionId, x.RequestId }).IsUnique();
            e.HasOne<Mission>().WithMany().HasForeignKey(x => x.MissionId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RobotQrScanEvent>(e =>
        {
            e.ToTable("RobotQrScanEvents"); e.HasKey(x => x.ScanId);
            e.Property(x => x.ScanId).HasColumnName("ScanID");
            e.Property(x => x.RobotId).HasColumnName("RobotID");
            e.Property(x => x.MissionId).HasColumnName("MissionID");
            e.Property(x => x.CheckpointId).HasColumnName("CheckpointID");
            e.HasIndex(x => new { x.RobotId, x.EventId }).IsUnique().HasFilter("\"EventId\" IS NOT NULL");
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId);
            e.HasOne<Mission>().WithMany().HasForeignKey(x => x.MissionId);
        });
    }
}
