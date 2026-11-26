using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
namespace SmartFleetBE;

public partial class SmartFleetDbContext
{
    public DbSet<StationCheckpoint> StationCheckpoints => Set<StationCheckpoint>();
    public DbSet<StationBerth> StationBerths => Set<StationBerth>();
    private static void ConfigureCatalog(ModelBuilder model)
    {
        model.Entity<Warehouse>().Property(w => w.Revision).IsConcurrencyToken();
        model.Entity<Zone>().Property(z => z.Revision).IsConcurrencyToken();
        model.Entity<Station>().Property(s => s.Revision).IsConcurrencyToken();
        model.Entity<StationCheckpoint>(e => {
            e.ToTable("StationCheckpoints"); e.HasKey(x => x.StationId);
            e.HasIndex(x => new { x.WarehouseId, x.ArucoId }).IsUnique();
            e.HasOne(x => x.Station).WithOne(s => s.Checkpoint).HasForeignKey<StationCheckpoint>(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<StationBerth>(e => {
            e.ToTable("StationBerths"); e.HasKey(x => x.StationId); e.HasIndex(x => x.RobotId).IsUnique();
            e.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
