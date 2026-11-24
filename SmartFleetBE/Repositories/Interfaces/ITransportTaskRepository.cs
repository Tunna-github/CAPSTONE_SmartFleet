using SmartFleetBE.Model;
using Microsoft.EntityFrameworkCore.Storage;

namespace SmartFleetBE.Repositories.Interfaces;

public interface ITransportTaskRepository
{
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TransportTask>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<TransportTask?> GetByIdAsync(
        long taskId,
        CancellationToken cancellationToken = default);

    Task<TransportTask?> GetByIdForUpdateAsync(
        long taskId,
        CancellationToken cancellationToken = default);

    Task<bool> IsWarehouseActiveAsync(
        int warehouseId,
        CancellationToken cancellationToken = default);

    Task<bool> IsStationActiveInWarehouseAsync(
        int stationId,
        int warehouseId,
        CancellationToken cancellationToken = default, string? stationType = null);

    Task<bool> HasOperationalDependenciesAsync(
        long taskId,
        CancellationToken cancellationToken = default);

    Task<bool> HasAssignmentsAsync(long taskId, CancellationToken cancellationToken = default);

    Task AddAsync(
        TransportTask task,
        CancellationToken cancellationToken = default);

    Task AddStatusHistoryAsync(
        TaskStatusHistory history,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        TransportTask task,
        CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default);
}
