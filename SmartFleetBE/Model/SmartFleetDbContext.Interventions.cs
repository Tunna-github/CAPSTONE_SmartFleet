using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE;

public partial class SmartFleetDbContext
{
    public DbSet<TaskIntervention> TaskInterventions => Set<TaskIntervention>();

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        ConfigureExecution(modelBuilder);
        modelBuilder.Entity<TaskIntervention>(e =>
        {
            e.ToTable("TaskInterventions");
            e.HasKey(x => x.TaskId);
            e.Property(x => x.State).HasMaxLength(40);
            e.Property(x => x.CommandId).HasMaxLength(36);
            e.HasOne<TransportTask>().WithOne().HasForeignKey<TaskIntervention>(x => x.TaskId);
        });
    }
}
