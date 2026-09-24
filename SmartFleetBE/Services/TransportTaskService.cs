using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;
using SmartFleetBE.Services.Results;

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
        return tasks.Select(MapToResponse).ToArray();
    }

    public async Task<TransportTaskResponse?> GetByIdAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await _transportTaskRepository.GetByIdAsync(taskId, cancellationToken);
        return task is null ? null : MapToResponse(task);
    }

    public async Task<TransportTaskServiceResult<TransportTaskResponse>> CreateAsync(
        CreateTransportTaskRequest request,
        int createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var validationError = await ValidateLocationAsync(
            request.WarehouseId,
            request.PickupStationId,
            request.DeliveryStationId,
            cancellationToken);

        if (validationError is not null)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .ValidationFailed(validationError);
        }

        var now = DateTime.UtcNow;
        var task = new TransportTask
        {
            TaskTrackingCode = GenerateTrackingCode(),
            WarehouseId = request.WarehouseId,
            PickupStationId = request.PickupStationId,
            DeliveryStationId = request.DeliveryStationId,
            PriorityLevel = request.PriorityLevel,
            TaskStatus = TransportTaskStatuses.Pending,
            PackageCode = NormalizeOptional(request.PackageCode),
            ItemDescription = NormalizeOptional(request.ItemDescription),
            PayloadWeightKg = request.PayloadWeightKg,
            CreatedBy = createdByUserId,
            CreatedAt = now,
            ScheduledTime = request.ScheduledTime
        };

        await _transportTaskRepository.AddAsync(task, cancellationToken);

        // Add the first lifecycle history entry in the same SaveChanges call.
        // EF Core will propagate the generated TaskId through the navigation.
        await _transportTaskRepository.AddStatusHistoryAsync(
            new TaskStatusHistory
            {
                Task = task,
                PreviousStatus = null,
                NewStatus = TransportTaskStatuses.Pending,
                ChangedBy = createdByUserId,
                Reason = "Transport task created.",
                ChangedAt = now
            },
            cancellationToken);

        await _transportTaskRepository.SaveChangesAsync(cancellationToken);

        var createdTask = await _transportTaskRepository.GetByIdAsync(
            task.TaskId,
            cancellationToken);

        return TransportTaskServiceResult<TransportTaskResponse>.Success(
            MapToResponse(createdTask ?? task));
    }

    public async Task<TransportTaskServiceResult<TransportTaskResponse>> UpdateAsync(
        long taskId,
        UpdateTransportTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var task = await _transportTaskRepository.GetByIdForUpdateAsync(
            taskId,
            cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .NotFound($"Transport task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Pending,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .Conflict("Only a PENDING task can be edited. Once it enters the dispatch lifecycle, its route and package data must not be changed through CRUD.");
        }

        var validationError = await ValidateLocationAsync(
            request.WarehouseId,
            request.PickupStationId,
            request.DeliveryStationId,
            cancellationToken);

        if (validationError is not null)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .ValidationFailed(validationError);
        }

        task.WarehouseId = request.WarehouseId;
        task.PickupStationId = request.PickupStationId;
        task.DeliveryStationId = request.DeliveryStationId;
        task.PriorityLevel = request.PriorityLevel;
        task.PackageCode = NormalizeOptional(request.PackageCode);
        task.ItemDescription = NormalizeOptional(request.ItemDescription);
        task.PayloadWeightKg = request.PayloadWeightKg;
        task.ScheduledTime = request.ScheduledTime;

        await _transportTaskRepository.SaveChangesAsync(cancellationToken);

        var updatedTask = await _transportTaskRepository.GetByIdAsync(
            taskId,
            cancellationToken);

        return TransportTaskServiceResult<TransportTaskResponse>.Success(
            MapToResponse(updatedTask ?? task));
    }

    public async Task<TransportTaskServiceResult<bool>> DeleteAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await _transportTaskRepository.GetByIdForUpdateAsync(
            taskId,
            cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<bool>
                .NotFound($"Transport task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Pending,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<bool>
                .Conflict("Only a PENDING task can be deleted. Tasks that have entered the dispatch or execution lifecycle must be cancelled by the task-status workflow instead of being removed from history.");
        }

        if (await _transportTaskRepository.HasOperationalDependenciesAsync(
                taskId,
                cancellationToken))
        {
            return TransportTaskServiceResult<bool>
                .Conflict("This task already has operational records and cannot be deleted safely.");
        }

        await _transportTaskRepository.DeleteAsync(task, cancellationToken);
        await _transportTaskRepository.SaveChangesAsync(cancellationToken);

        return TransportTaskServiceResult<bool>.Success(true);
    }

    private async Task<string?> ValidateLocationAsync(
        int warehouseId,
        int pickupStationId,
        int deliveryStationId,
        CancellationToken cancellationToken)
    {
        if (pickupStationId == deliveryStationId)
        {
            return "Pickup station and delivery station must be different.";
        }

        if (!await _transportTaskRepository.IsWarehouseActiveAsync(
                warehouseId,
                cancellationToken))
        {
            return $"Warehouse {warehouseId} does not exist or is inactive.";
        }

        if (!await _transportTaskRepository.IsStationActiveInWarehouseAsync(
                pickupStationId,
                warehouseId,
                cancellationToken))
        {
            return $"Pickup station {pickupStationId} does not exist, is inactive, or does not belong to warehouse {warehouseId}.";
        }

        if (!await _transportTaskRepository.IsStationActiveInWarehouseAsync(
                deliveryStationId,
                warehouseId,
                cancellationToken))
        {
            return $"Delivery station {deliveryStationId} does not exist, is inactive, or does not belong to warehouse {warehouseId}.";
        }

        return null;
    }

    private static TransportTaskResponse MapToResponse(TransportTask task)
    {
        return new TransportTaskResponse
        {
            TaskId = task.TaskId,
            TaskTrackingCode = task.TaskTrackingCode,
            WarehouseId = task.WarehouseId,
            WarehouseName = task.Warehouse?.WarehouseName ?? string.Empty,
            PickupStationId = task.PickupStationId,
            PickupStationName = task.PickupStation?.StationName ?? string.Empty,
            DeliveryStationId = task.DeliveryStationId,
            DeliveryStationName = task.DeliveryStation?.StationName ?? string.Empty,
            PriorityLevel = task.PriorityLevel,
            TaskStatus = task.TaskStatus,
            PackageCode = task.PackageCode,
            ItemDescription = task.ItemDescription,
            PayloadWeightKg = task.PayloadWeightKg,
            CreatedBy = task.CreatedBy,
            CreatedAt = task.CreatedAt,
            ScheduledTime = task.ScheduledTime,
            QueuedAt = task.QueuedAt,
            AssignedAt = task.AssignedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            CancelledAt = task.CancelledAt,
            FailureReason = task.FailureReason,
            CancellationReason = task.CancellationReason
        };
    }

    private static string GenerateTrackingCode()
    {
        return $"TASK-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..36].ToUpperInvariant();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
