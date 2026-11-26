using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore.Storage;

namespace SmartFleetBE.Repositories;

public sealed class TransportTaskRepository : ITransportTaskRepository
{
    public Task<SmartFleetBE.Services.TaskLocationPolicy.StationRoute> ResolveStationRouteAsync(int warehouseId, int pickupId, int deliveryId,
        SmartFleetBE.Services.QrDemoRouteService routes, CancellationToken ct) =>
        SmartFleetBE.Services.TaskLocationPolicy.ResolveRouteAsync(_dbContext, warehouseId, pickupId, deliveryId, routes, ct);
    private readonly SmartFleetDbContext _dbContext;

    public TransportTaskRepository(SmartFleetDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

    public async Task<IReadOnlyCollection<TransportTask>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await BaseQuery(asNoTracking: true)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<TransportTask?> GetByIdAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        return BaseQuery(asNoTracking: true)
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
    }

    public Task<TransportTask?> GetByIdForUpdateAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        return BaseQuery(asNoTracking: false)
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
    }

    public Task<bool> IsWarehouseActiveAsync(
        int warehouseId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Warehouses
            .AsNoTracking()
            .AnyAsync(
                w => w.WarehouseId == warehouseId && w.IsActive,
                cancellationToken);
    }

    public Task<bool> IsStationActiveInWarehouseAsync(
        int stationId,
        int warehouseId,
        CancellationToken cancellationToken = default, string? stationType = null)
    {
        return _dbContext.Stations
            .AsNoTracking()
            .AnyAsync(
                s => s.StationId == stationId
                     && (stationType == null || s.StationType == stationType || s.StationType == "TRANSFER" && (stationType == "PICKUP" || stationType == "DELIVERY"))
                     && s.IsActive
                     && s.Zone.IsActive
                     && s.Zone.WarehouseId == warehouseId,
                cancellationToken);
    }

    public async Task<bool> HasOperationalDependenciesAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        if (await _dbContext.TaskAssignments
                .AsNoTracking()
                .AnyAsync(a => a.TaskId == taskId, cancellationToken))
        {
            return true;
        }

        if (await _dbContext.Notifications
                .AsNoTracking()
                .AnyAsync(n => n.RelatedTaskId == taskId, cancellationToken))
        {
            return true;
        }

        return await _dbContext.RobotIncidents
            .AsNoTracking()
            .AnyAsync(i => i.TaskId == taskId, cancellationToken);
    }

    public Task<bool> HasAssignmentsAsync(long taskId, CancellationToken cancellationToken = default) =>
        _dbContext.TaskAssignments.AnyAsync(a => a.TaskId == taskId, cancellationToken);

    public async Task AddAsync(
        TransportTask task,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TransportTasks.AddAsync(task, cancellationToken);
    }

    public async Task AddStatusHistoryAsync(
        TaskStatusHistory history,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskStatusHistories.AddAsync(history, cancellationToken);
    }

    public async Task DeleteAsync(
        TransportTask task,
        CancellationToken cancellationToken = default)
    {
        // TaskStatusHistories use a non-cascading FK, so remove history rows
        // together with a task that is still safe to hard-delete.
        var histories = await _dbContext.TaskStatusHistories
            .Where(h => h.TaskId == task.TaskId)
            .ToListAsync(cancellationToken);

        if (histories.Count > 0)
        {
            _dbContext.TaskStatusHistories.RemoveRange(histories);
        }

        _dbContext.TransportTasks.Remove(task);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<TransportTask> BaseQuery(bool asNoTracking)
    {
        IQueryable<TransportTask> query = _dbContext.TransportTasks
            .Include(t => t.Warehouse)
            .Include(t => t.PickupStation)
            .Include(t => t.DeliveryStation);
        query = query.Include(t => t.TaskAssignments).ThenInclude(a => a.Robot);

        return asNoTracking ? query.AsNoTracking() : query;
    }
}
