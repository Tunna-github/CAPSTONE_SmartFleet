using SmartFleetBE.Model;

namespace SmartFleetBE.Repositories.Interfaces;

public interface ITransportTaskRepository
{
    Task<IReadOnlyCollection<TransportTask>> GetAllAsync(CancellationToken cancellationToken = default);
}
