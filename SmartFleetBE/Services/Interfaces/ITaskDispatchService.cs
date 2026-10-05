using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Services.Interfaces;

public interface ITaskDispatchService
{
    Task<
        TransportTaskServiceResult<
            IReadOnlyCollection<AvailableRobotResponse>>>
        GetAvailableRobotsAsync(
            long taskId,
            CancellationToken cancellationToken = default);


    Task<
        TransportTaskServiceResult<ManualAssignTaskResponse>>
        AssignManualAsync(
            long taskId,
            int robotId,
            int operatorUserId,
            CancellationToken cancellationToken = default);
}