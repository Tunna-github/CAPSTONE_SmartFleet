using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public sealed class TransportTaskService : ITransportTaskService
{
    private readonly ITransportTaskRepository _transportTaskRepository;

    public TransportTaskService(ITransportTaskRepository transportTaskRepository)
    {
        _transportTaskRepository = transportTaskRepository;
    }

    public async Task<IReadOnlyCollection<TransportTaskResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = await _transportTaskRepository.GetAllAsync(cancellationToken);

        return tasks.Select(task => new TransportTaskResponse
        {
            TaskId = task.TaskId,
            TaskTrackingCode = task.TaskTrackingCode,
            WarehouseId = task.WarehouseId,
            PickupStationId = task.PickupStationId,
            DeliveryStationId = task.DeliveryStationId,
            PriorityLevel = task.PriorityLevel,
            TaskStatus = task.TaskStatus,
            PackageCode = task.PackageCode,
            CreatedAt = task.CreatedAt
        }).ToArray();
    }
}
