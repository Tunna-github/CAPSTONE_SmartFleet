using SmartFleetBE.DTOs.TransportTasks;

namespace SmartFleetBE.Services.Interfaces;

public interface ITransportTaskService
{
    Task<IReadOnlyCollection<TransportTaskResponse>> GetAllAsync(
        CancellationToken cancellationToken = default);
}
