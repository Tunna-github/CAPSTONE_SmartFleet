using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Services.Interfaces;

public interface ITransportTaskService
{
    Task<IReadOnlyCollection<TransportTaskResponse>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<TransportTaskResponse?> GetByIdAsync(
        long taskId,
        CancellationToken cancellationToken = default);

    Task<TransportTaskServiceResult<TransportTaskResponse>> CreateAsync(
        CreateTransportTaskRequest request,
        int createdByUserId,
        CancellationToken cancellationToken = default);

    Task<TransportTaskServiceResult<TransportTaskResponse>> UpdateAsync(
        long taskId,
        UpdateTransportTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<TransportTaskServiceResult<bool>> DeleteAsync(
        long taskId,
        CancellationToken cancellationToken = default);
}
