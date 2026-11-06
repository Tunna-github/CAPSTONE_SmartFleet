
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;

namespace SmartFleetBE.Services;

/// <summary>
/// Periodically dispatches due AUTO tasks.
/// MANUAL tasks are never selected by this worker.
/// </summary>
public sealed class ScheduledAutoDispatchWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ScheduledAutoDispatchWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // Default: check every 5 seconds.
        // Clamp interval to prevent an overly aggressive poll.
        int pollSeconds = Math.Clamp(
            configuration.GetValue<int?>(
                "AutoDispatch:PollSeconds") ?? 5,
            2,
            60);

        var interval = TimeSpan.FromSeconds(pollSeconds);

        logger.LogInformation(
            "ScheduledAutoDispatchWorker started. " +
            "Polling every {Seconds} seconds.",
            pollSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            // Run immediately at startup.
            // Then continue at the configured interval.
            do
            {
                try
                {
                    await ProcessDueTasksAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Scheduled Auto Dispatch polling failed. " +
                        "The next polling cycle will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(
                stoppingToken));
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }

        logger.LogInformation(
            "ScheduledAutoDispatchWorker stopped.");
    }

    // =====================================================
    // FIND TASKS WHOSE SCHEDULED TIME HAS ARRIVED
    // =====================================================

    private async Task ProcessDueTasksAsync(
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<SmartFleetDbContext>();

        var autoDispatch = scope.ServiceProvider
            .GetRequiredService<AutoDispatchService>();

        var utcNow = DateTime.UtcNow;

        // Eligible Task requirements:
        // 1. DispatchMode = AUTO
        // 2. TaskStatus = QUEUED
        // 3. ScheduledTime is null or has arrived
        //
        // Order:
        // - Higher priority first
        // - Earlier schedule first
        // - Earlier creation first
        //
        // No Take(N) limit to avoid permanently
        // starving tasks outside the first page.

        var dueTaskIds = await db.TransportTasks
            .AsNoTracking()
            .Where(t =>
                t.DispatchMode == "AUTO" &&

                t.TaskStatus ==
                    TransportTaskStatuses.Queued &&

                (
                    t.ScheduledTime == null ||
                    t.ScheduledTime <= utcNow
                ))
            .OrderByDescending(t => t.PriorityLevel)
            .ThenBy(t => t.ScheduledTime)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.TaskId)
            .Select(t => t.TaskId)
            .ToListAsync(cancellationToken);

        if (dueTaskIds.Count == 0)
        {
            return;
        }

        logger.LogDebug(
            "Scheduled Auto Dispatch found {Count} due Tasks.",
            dueTaskIds.Count);

        // =================================================
        // TRY ASSIGNMENT FOR EACH DUE TASK
        // =================================================

        foreach (var taskId in dueTaskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Reuses the AutoDispatchService we already built.
                //
                // That service:
                // - Rechecks Task status and schedule
                // - Checks robot battery, load, status,
                //   maintenance and active assignments
                // - Selects an eligible robot
                // - Creates AUTO Assignment and Mission
                // - Saves MQTT command in Outbox
                // - Uses a serializable DB transaction

                bool assigned =
                    await autoDispatch.TryAssignAsync(
                        taskId,
                        cancellationToken);

                if (assigned)
                {
                    logger.LogInformation(
                        "SCHEDULED AUTO DISPATCH SUCCESS: " +
                        "Task {TaskId} has been assigned.",
                        taskId);
                }

                // If no robot is available, the Task
                // stays QUEUED and will be retried
                // on the next polling cycle.
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One failed Task must not stop all
                // other Tasks from being dispatched.
                logger.LogError(
                    exception,
                    "Scheduled dispatch failed for Task {TaskId}. " +
                    "The Task will be rechecked if still QUEUED.",
                    taskId);
            }
        }
    }
}
