
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Model;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;
using SmartFleetBE.Services.Results;
using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Services;

public sealed class TransportTaskService : ITransportTaskService
{
    private readonly ITransportTaskRepository _repository;
    private readonly AutoDispatchService _autoDispatch;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TransportTaskService> _logger;
    private readonly QrDemoRouteService? _routes;

    public TransportTaskService(
        ITransportTaskRepository transportTaskRepository,
        AutoDispatchService autoDispatchService,
        IConfiguration configuration,
        ILogger<TransportTaskService> logger, QrDemoRouteService? routes = null)
    {
        _repository = transportTaskRepository;
        _autoDispatch = autoDispatchService;
        _configuration = configuration;
        _logger = logger;
        _routes = routes;
    }

    // =====================================================
    // GET ALL TASKS
    // =====================================================

    public async Task<IReadOnlyCollection<TransportTaskResponse>>
        GetAllAsync(
            CancellationToken cancellationToken = default)
    {
        var tasks = await _repository.GetAllAsync(cancellationToken);

        return tasks.Select(MapToResponse).ToArray();
    }

    // =====================================================
    // GET TASK BY ID
    // =====================================================

    public async Task<TransportTaskResponse?> GetByIdAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await _repository.GetByIdAsync(
            taskId, cancellationToken);

        return task is null ? null : MapToResponse(task);
    }

    // =====================================================
    // CREATE TASK WITH AUTO / MANUAL / SCHEDULED MODE
    // =====================================================

    public async Task<
        TransportTaskServiceResult<TransportTaskResponse>>
        CreateAsync(
            CreateTransportTaskRequest request,
            int createdByUserId,
            CancellationToken cancellationToken = default)
    {
        // STEP 1: VALIDATE DISPATCH MODE

        bool isAuto = string.Equals(
            request.DispatchMode,
            "AUTO",
            StringComparison.Ordinal);

        bool isManual = string.Equals(
            request.DispatchMode,
            "MANUAL",
            StringComparison.Ordinal);

        if (!isAuto && !isManual)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .ValidationFailed(
                    "DispatchMode must be AUTO or MANUAL.");
        }

        // STEP 2: VALIDATE WEIGHT

        if (request.PayloadWeightKg is not > 0m)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .ValidationFailed(
                    "PayloadWeightKg must be greater than zero.");
        }

        // STEP 3: CHECK AUTO DISPATCH SETTINGS

        if (isAuto)
        {
            if (!_configuration.GetValue<bool>(
                    "AutoDispatch:EnableOnCreate"))
            {
                return TransportTaskServiceResult<TransportTaskResponse>
                    .ValidationFailed(
                        "AUTO dispatch is disabled.");
            }

            if (!_configuration.GetValue<bool>(
                    "Mqtt:EnableWorkers"))
            {
                return TransportTaskServiceResult<TransportTaskResponse>
                    .ValidationFailed(
                        "AUTO requires Mqtt:EnableWorkers=true.");
            }

            var verifiedLimitKg =
                _configuration.GetValue<decimal>(
                    "AutoDispatch:VerifiedPayloadLimitKg");

            if (verifiedLimitKg <= 0m)
            {
                return TransportTaskServiceResult<TransportTaskResponse>
                    .ValidationFailed(
                        "Configure the verified robot payload limit.");
            }
        }

        // STEP 4: VALIDATE WAREHOUSE AND STATIONS

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

        var qrPilot = _configuration.GetValue<bool>("DemoRoute:Enabled");
        if (qrPilot)
        {
            if (_routes == null || !_routes.SupportsWarehouse(request.WarehouseId) ||
                request.PickupArUcoId == null || request.DeliveryArUcoId == null)
                return TransportTaskServiceResult<TransportTaskResponse>.ValidationFailed(
                    "Select pickup and delivery QR markers for this navigation warehouse.");
            try { _routes.ValidateTaskSelection(request.PickupArUcoId.Value, request.DeliveryArUcoId.Value); }
            catch (ArgumentException ex) { return TransportTaskServiceResult<TransportTaskResponse>.ValidationFailed(ex.Message); }
        }

        // STEP 5: CREATE TASK

        var now = DateTime.UtcNow;

        var task = new TransportTask
        {
            TaskTrackingCode = GenerateTrackingCode(),

            WarehouseId = request.WarehouseId,
            PickupStationId = request.PickupStationId,
            DeliveryStationId = request.DeliveryStationId,

            PriorityLevel = request.PriorityLevel,

            TaskStatus = TransportTaskStatuses.Queued,

            // NEW: persisted to the database.
            DispatchMode = request.DispatchMode,

            QueuedAt = now,

            PackageCode =
                NormalizeOptional(request.PackageCode),

            ItemDescription =
                NormalizeOptional(request.ItemDescription),

            PayloadWeightKg = request.PayloadWeightKg,

            CreatedBy = createdByUserId,
            CreatedAt = now,

            // Frontend already sends UTC ISO 8601.
            ScheduledTime = request.ScheduledTime
        };

        await using (var creation = await _repository.BeginTransactionAsync(cancellationToken))
        {
        await _repository.AddAsync(
            task, cancellationToken);

        // STEP 6: CREATE STATUS HISTORY

        await _repository.AddStatusHistoryAsync(
            new TaskStatusHistory
            {
                Task = task,

                PreviousStatus = null,
                NewStatus = TransportTaskStatuses.Queued,

                ChangedBy = createdByUserId,

                Reason = isAuto
                    ? "Task created with AUTO dispatch mode."
                    : "Task created with MANUAL dispatch mode.",

                ChangedAt = now
            },
            cancellationToken);

        // STEP 7: COMMIT TASK FIRST
        //
        // Even if no robot is eligible, the Task must
        // remain in the database as QUEUED.

        await _repository.SaveChangesAsync(
            cancellationToken);
        // Configure the route before the QUEUED row becomes visible to AUTO's
        // worker. A file/DB failure rolls back creation; no half-configured task
        // can be assigned between two frontend requests.
        if (qrPilot)
        {
            try { _routes!.ConfigureTask(task.TaskId, request.PickupArUcoId!.Value, request.DeliveryArUcoId!.Value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return TransportTaskServiceResult<TransportTaskResponse>.ValidationFailed(
                    "Task was not created: QR route storage failed. Retry after fixing storage.");
            }
        }
        await creation.CommitAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Task {TaskId} created: Mode={Mode}, " +
            "ScheduledTime={ScheduledTime}.",
            task.TaskId,
            task.DispatchMode,
            task.ScheduledTime);

        // STEP 8: DISPATCH DECISION

        if (isAuto)
        {
            var currentUtc = DateTime.UtcNow;

            bool scheduledForFuture =
                task.ScheduledTime.HasValue &&
                task.ScheduledTime.Value > currentUtc;

            if (scheduledForFuture)
            {
                // The worker will automatically pick
                // this Task up when ScheduledTime arrives.

                _logger.LogInformation(
                    "Task {TaskId} is scheduled for {ScheduledTime}. " +
                    "Waiting for ScheduledAutoDispatchWorker.",
                    task.TaskId,
                    task.ScheduledTime);
            }
            else
            {
                // No future schedule: try immediately.
                // If no robot is eligible, worker will
                // retry while the Task remains QUEUED.

                try
                {
                    bool assigned =
                        await _autoDispatch.TryAssignNextAsync(cancellationToken);

                    _logger.LogInformation(
                        "Immediate AUTO queue pass after creating Task {TaskId}: {Result}.",
                        task.TaskId,
                        assigned ? "DISPATCHED_NEXT_ELIGIBLE" : "QUEUED");
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Immediate AUTO dispatch failed for " +
                        "Task {TaskId}. Worker can retry.",
                        task.TaskId);
                }
            }
        }
        else
        {
            // MANUAL Tasks are never automatically assigned.

            _logger.LogInformation(
                "Task {TaskId} created as MANUAL. " +
                "Waiting for Warehouse Operator.",
                task.TaskId);
        }

        // STEP 9: RETURN LATEST TASK STATUS

        var createdTask = await _repository.GetByIdAsync(
            task.TaskId, cancellationToken);

        return TransportTaskServiceResult<TransportTaskResponse>
            .Success(MapToResponse(createdTask ?? task));
    }

    // =====================================================
    // UPDATE TASK: KEEP THE CURRENT CRUD POLICY
    // =====================================================

    public async Task<
        TransportTaskServiceResult<TransportTaskResponse>>
        UpdateAsync(
            long taskId,
            UpdateTransportTaskRequest request,
            CancellationToken cancellationToken = default)
    {
        var task = await _repository.GetByIdForUpdateAsync(
            taskId, cancellationToken);

        if (task is null)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .NotFound(
                    $"Transport task {taskId} was not found.");
        }

        if (task.TaskStatus is not (TransportTaskStatuses.Pending or TransportTaskStatuses.Queued) ||
            await _repository.HasAssignmentsAsync(taskId, cancellationToken))
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .Conflict(
                    "Only an unassigned PENDING or QUEUED task can be edited. " +
                    "Previously dispatched tasks must use the recovery workflow.");
        }

        if (request.PayloadWeightKg is not > 0m)
            return TransportTaskServiceResult<TransportTaskResponse>
                .ValidationFailed("PayloadWeightKg must be greater than zero.");

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

        task.PackageCode =
            NormalizeOptional(request.PackageCode);

        task.ItemDescription =
            NormalizeOptional(request.ItemDescription);

        task.PayloadWeightKg = request.PayloadWeightKg;
        task.ScheduledTime = request.ScheduledTime;

        try { await _repository.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return TransportTaskServiceResult<TransportTaskResponse>
                .Conflict("Task changed during editing. Reload before trying again.");
        }

        var updated = await _repository.GetByIdAsync(
            taskId, cancellationToken);

        return TransportTaskServiceResult<TransportTaskResponse>
            .Success(MapToResponse(updated ?? task));
    }

    // =====================================================
    // DELETE TASK: KEEP THE CURRENT CRUD POLICY
    // =====================================================

    public async Task<TransportTaskServiceResult<bool>>
        DeleteAsync(
            long taskId,
            CancellationToken cancellationToken = default)
    {
        var task = await _repository.GetByIdForUpdateAsync(
            taskId, cancellationToken);

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
                    "Tasks in the dispatch lifecycle must be " +
                    "cancelled through the status workflow.");
        }

        if (await _repository.HasOperationalDependenciesAsync(
                taskId, cancellationToken))
        {
            return TransportTaskServiceResult<bool>
                .Conflict(
                    "This task has operational records " +
                    "and cannot be deleted safely.");
        }

        await _repository.DeleteAsync(
            task, cancellationToken);

        await _repository.SaveChangesAsync(
            cancellationToken);

        return TransportTaskServiceResult<bool>
            .Success(true);
    }

    // =====================================================
    // WAREHOUSE AND STATION VALIDATION
    // =====================================================

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

        if (!await _repository.IsWarehouseActiveAsync(
                warehouseId, cancellationToken))
        {
            return $"Warehouse {warehouseId} does not exist " +
                   "or is inactive.";
        }

        if (!await _repository.IsStationActiveInWarehouseAsync(
                pickupStationId, warehouseId, cancellationToken, "PICKUP"))
        {
            return $"Pickup station {pickupStationId} does not " +
                   $"exist, is inactive, or does not belong " +
                   $"to warehouse {warehouseId}, or is not a PICKUP station.";
        }

        if (!await _repository.IsStationActiveInWarehouseAsync(
                deliveryStationId, warehouseId, cancellationToken, "DELIVERY"))
        {
            return $"Delivery station {deliveryStationId} does not " +
                   $"exist, is inactive, or does not belong " +
                   $"to warehouse {warehouseId}, or is not a DELIVERY station.";
        }

        return null;
    }

    // =====================================================
    // MAP TO RESPONSE
    // =====================================================

    private static TransportTaskResponse MapToResponse(
        TransportTask task)
    {
        return new TransportTaskResponse
        {
            TaskId = task.TaskId,
            TaskTrackingCode = task.TaskTrackingCode,

            WarehouseId = task.WarehouseId,
            WarehouseName =
                task.Warehouse?.WarehouseName ?? string.Empty,

            PickupStationId = task.PickupStationId,
            PickupStationName =
                task.PickupStation?.StationName ?? string.Empty,

            DeliveryStationId = task.DeliveryStationId,
            DeliveryStationName =
                task.DeliveryStation?.StationName ?? string.Empty,

            PriorityLevel = task.PriorityLevel,
            TaskStatus = task.TaskStatus,
            DispatchMode = task.DispatchMode,
            AssignedRobotId = task.TaskAssignments.FirstOrDefault(a => a.AssignmentStatus == "ACTIVE")?.Robot?.RobotCode,

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

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
