using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Services.Interfaces
{
    public interface ITaskDispatchService
    {
        Task<TransportTaskServiceResult<ManualAssignTaskResponse>> AssignManualAsync(
        long taskId,
        int robotId,
        string movementPattern,
        int operatorUserId,
        CancellationToken cancellationToken = default);
    }
}
