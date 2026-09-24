using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;

namespace SmartFleetBE.Repositories;

public sealed class MaintenanceRecordRepository : IMaintenanceRecordRepository
{
    private readonly SmartFleetDbContext _dbContext;

    public MaintenanceRecordRepository(SmartFleetDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<RobotMaintenanceRecord>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RobotMaintenanceRecords
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
