
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Services;

public sealed class TransportTaskService : ITransportTaskService
{
    private readonly ITransportTaskRepository
        _transportTaskRepository;

    private readonly AutoDispatchService
        _autoDispatchService;

    private readonly IConfiguration
        _configuration;

    private readonly ILogger<TransportTaskService>
        _logger;

    public TransportTaskService(
        ITransportTaskRepository transportTaskRepository,
        AutoDispatchService autoDispatchService,
        IConfiguration configuration,
        ILogger<TransportTaskService> logger)
    {
        _transportTaskRepository =
            transportTaskRepository;

        _autoDispatchService =
            autoDispatchService;

        _configuration = configuration;

        _logger = logger;
    }

    // ================================================
    // GET ALL TASKS
    // ================================================

    public async Task<IReadOnlyCollection<TransportTaskResponse>>
        GetAllAsync(
            CancellationToken cancellationToken = default)
    {
        var tasks =
            await _transportTaskRepository.GetAllAsync(
                cancellationToken);

        return tasks.Select(MapToResponse).ToArray();
    }

    // ================================================
    // GET TASK BY ID
    // ================================================

    public async Task<TransportTaskResponse?>
        GetByIdAsync(
            long taskId,
            CancellationToken cancellationToken = default)
    {
        var task =
            await _transportTaskRepository.GetByIdAsync(
                taskId,
                cancellationToken);

        return task is null
            ? null
            : MapToResponse(task);
    }

    // ================================================
    // CREATE TASK + AUTO DISPATCH
    // ================================================

    public async Task<
        TransportTaskServiceResult<TransportTaskResponse>>
        CreateAsync(
            CreateTransportTaskRequest request,
            int createdByUserId,
            CancellationToken cancellationToken = default)
    {
        // STEP 1: VALIDATE LOCATIONS

        var validationError =
            await ValidateLocationAsync(
                request.WarehouseId,
                request.PickupStationId,
                request.DeliveryStationId,
                cancellationToken);

        if (validationError is not null)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .ValidationFailed(validationError);
        }

        // STEP 2: CREATE QUEUED TASK

        var now = DateTime.UtcNow;

        var task = new TransportTask
        {
            TaskTrackingCode = GenerateTrackingCode(),

            WarehouseId = request.WarehouseId,

            PickupStationId = request.PickupStationId,

            DeliveryStationId = request.DeliveryStationId,

            PriorityLevel = request.PriorityLevel,

            TaskStatus = TransportTaskStatuses.Queued,

            QueuedAt = now,

            PackageCode =
                NormalizeOptional(request.PackageCode),

            ItemDescription =
                NormalizeOptional(request.ItemDescription),

            PayloadWeightKg = request.PayloadWeightKg,

            CreatedBy = createdByUserId,

            CreatedAt = now,

            ScheduledTime = request.ScheduledTime
        };

        await _transportTaskRepository.AddAsync(
            task,
            cancellationToken);

        // STEP 3: RECORD INITIAL TASK STATUS

        await _transportTaskRepository.AddStatusHistoryAsync(
            new TaskStatusHistory
            {
                Task = task,

                PreviousStatus = null,

                NewStatus = TransportTaskStatuses.Queued,

                ChangedBy = createdByUserId,

                Reason =
                    "Transport task created and queued " +
                    "for dispatch.",

                ChangedAt = now
            },
            cancellationToken);

        // STEP 4: SAVE TASK FIRST
        // If no robot qualifies, Task remains QUEUED.

        await _transportTaskRepository.SaveChangesAsync(
            cancellationToken);

        // STEP 5: TRY AUTOMATIC ASSIGNMENT

        if (_configuration.GetValue<bool>(
                "AutoDispatch:EnableOnCreate"))
        {
            if (!_configuration.GetValue<bool>(
                    "Mqtt:EnableWorkers"))
            {
                _logger.LogWarning(
                    "AUTO DISPATCH skipped for Task {TaskId}: " +
                    "Mqtt:EnableWorkers=false.",
                    task.TaskId);
            }
            else
            {
                try
                {
                    var assigned =
                        await _autoDispatchService.TryAssignAsync(
                            task.TaskId,
                            cancellationToken);

                    if (!assigned)
                    {
                        _logger.LogInformation(
                            "Task {TaskId} stays QUEUED: " +
                            "no eligible robot or dispatch " +
                            "conditions not met.",
                            task.TaskId);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The Task was saved before dispatch.
                    // Check the DB if assignment result
                    // is uncertain after a connection error.

                    _logger.LogError(
                        exception,
                        "AUTO DISPATCH failed for Task {TaskId}; " +
                        "check Task/Assignment in database.",
                        task.TaskId);
                }
            }
        }

        // STEP 6: READ THE LATEST TASK STATUS
        //
        // ASSIGNED if auto dispatch succeeded.
        // QUEUED if no robot was selected.

        var createdTask =
            await _transportTaskRepository.GetByIdAsync(
                task.TaskId,
                cancellationToken);

        return TransportTaskServiceResult<TransportTaskResponse>
            .Success(
                MapToResponse(createdTask ?? task));
    }

    // ================================================
    // UPDATE TASK
    // ================================================

    public async Task<
        TransportTaskServiceResult<TransportTaskResponse>>
        UpdateAsync(
            long taskId,
            UpdateTransportTaskRequest request,
            CancellationToken cancellationToken = default)
    {
        var task =
            await _transportTaskRepository.GetByIdForUpdateAsync(
                taskId,
                cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .NotFound(
                    $"Transport task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Pending,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .Conflict(
                    "Only a PENDING task can be edited. " +
                    "Once it enters the dispatch lifecycle, " +
                    "its route and package data must not be " +
                    "changed through CRUD.");
        }

        var validationError =
            await ValidateLocationAsync(
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

        task.PackageCode =
            NormalizeOptional(request.PackageCode);

        task.ItemDescription =
            NormalizeOptional(request.ItemDescription);

        task.PayloadWeightKg = request.PayloadWeightKg;
        task.ScheduledTime = request.ScheduledTime;

        await _transportTaskRepository.SaveChangesAsync(
            cancellationToken);

        var updatedTask =
            await _transportTaskRepository.GetByIdAsync(
                taskId,
                cancellationToken);

        return TransportTaskServiceResult<TransportTaskResponse>
            .Success(MapToResponse(updatedTask ?? task));
    }

    // ================================================
    // DELETE TASK
    // ================================================

    public async Task<
        TransportTaskServiceResult<bool>>
        DeleteAsync(
            long taskId,
            CancellationToken cancellationToken = default)
    {
        var task =
            await _transportTaskRepository.GetByIdForUpdateAsync(
                taskId,
                cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<bool>
                .NotFound(
                    $"Transport task {taskId} was not found.");
        }

        if (!string.Equals(
                task.TaskStatus,
                TransportTaskStatuses.Pending,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<bool>
                .Conflict(
                    "Only a PENDING task can be deleted. " +
                    "Tasks that entered the dispatch lifecycle " +
                    "must be cancelled through the " +
                    "task-status workflow.");
        }

        if (await _transportTaskRepository
                .HasOperationalDependenciesAsync(
                    taskId,
                    cancellationToken))
        {
            return TransportTaskServiceResult<bool>
                .Conflict(
                    "This task has operational records " +
                    "and cannot be safely deleted.");
        }

        await _transportTaskRepository.DeleteAsync(
            task,
            cancellationToken);

        await _transportTaskRepository.SaveChangesAsync(
            cancellationToken);

        return TransportTaskServiceResult<bool>
            .Success(true);
    }

    // ================================================
    // VALIDATE WAREHOUSE AND STATIONS
    // ================================================

    private async Task<string?> ValidateLocationAsync(
        int warehouseId,
        int pickupStationId,
        int deliveryStationId,
        CancellationToken cancellationToken)
    {
        if (pickupStationId == deliveryStationId)
        {
            return "Pickup station and delivery station " +
                   "must be different.";
        }

        if (!await _transportTaskRepository
                .IsWarehouseActiveAsync(
                    warehouseId,
                    cancellationToken))
        {
            return $"Warehouse {warehouseId} does not exist " +
                   "or is inactive.";
        }

        if (!await _transportTaskRepository
                .IsStationActiveInWarehouseAsync(
                    pickupStationId,
                    warehouseId,
                    cancellationToken))
        {
            return $"Pickup station {pickupStationId} " +
                   $"does not exist, is inactive, or " +
                   $"does not belong to warehouse {warehouseId}.";
        }

        if (!await _transportTaskRepository
                .IsStationActiveInWarehouseAsync(
                    deliveryStationId,
                    warehouseId,
                    cancellationToken))
        {
            return $"Delivery station {deliveryStationId} " +
                   $"does not exist, is inactive, or " +
                   $"does not belong to warehouse {warehouseId}.";
        }

        return null;
    }

    // ================================================
    // MAP ENTITY TO API RESPONSE
    // ================================================

    private static TransportTaskResponse MapToResponse(
        TransportTask task)
    {
        return new TransportTaskResponse
        {
            TaskId = task.TaskId,

            TaskTrackingCode = task.TaskTrackingCode,

            WarehouseId = task.WarehouseId,

            WarehouseName =
                task.Warehouse?.WarehouseName ??
                string.Empty,

            PickupStationId = task.PickupStationId,

            PickupStationName =
                task.PickupStation?.StationName ??
                string.Empty,

            DeliveryStationId = task.DeliveryStationId,

            DeliveryStationName =
                task.DeliveryStation?.StationName ??
                string.Empty,

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
        return (
            $"TASK-{DateTime.UtcNow:yyyyMMddHHmmss}-" +
            $"{Guid.NewGuid():N}"
        )[..36].ToUpperInvariant();
    }

    private static string? NormalizeOptional(
        string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
