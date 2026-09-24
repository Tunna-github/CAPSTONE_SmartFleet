using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;

namespace SmartFleetBE.Repositories;

public sealed class TransportTaskRepository : ITransportTaskRepository
{
    private readonly SmartFleetDbContext _dbContext;

    public TransportTaskRepository(SmartFleetDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<TransportTask>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TransportTasks
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
