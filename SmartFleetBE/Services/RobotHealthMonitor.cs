using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public sealed class RobotHealthMonitor(SmartFleetDbContext db, IConfiguration config,
    TaskInterventionService interventions, ILogger<RobotHealthMonitor>? logger = null)
{
    public async Task<IReadOnlyList<string>> CheckAsync(DateTime now, CancellationToken ct = default)
    {
        var cutoff = now.AddSeconds(-RobotPresencePolicy.TimeoutSeconds(config));
        var ackCutoff = now.AddSeconds(-Math.Clamp(config.GetValue("Dispatch:AssignmentAckTimeoutSeconds", 30), 10, 300));
        var robots = await db.Robots.Where(r => r.IsActive && (r.ConnectionStatus == "ONLINE" ||
            db.TaskAssignments.Any(a => a.RobotId == r.RobotId && a.AssignmentStatus == "ACTIVE")) &&
            (r.LastHeartbeatTime == null || r.LastHeartbeatTime < cutoff ||
             r.LastTelemetryTime == null || r.LastTelemetryTime < cutoff || r.BatteryPercent < 20 ||
             db.Missions.Any(m => m.Assignment.RobotId == r.RobotId && m.Assignment.AssignmentStatus == "ACTIVE" &&
                 m.MissionStatus == "INITIALIZING" && m.CreatedAt < ackCutoff &&
                 !db.TaskInterventions.Any(c => c.TaskId == m.Assignment.TaskId && (c.Paused || c.State != "NORMAL")))))
            .Select(r => r.RobotId).ToListAsync(ct);
        var changed = new List<string>();
        foreach (var id in robots)
        {
          try
          {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var robot = await db.Robots.SingleAsync(r => r.RobotId == id, ct);
            var offline = !RobotPresencePolicy.IsFresh(robot, now, config);
            var critical = !offline && robot.BatteryPercent < 10;
            var previous = robot.OperationalStatus;
            var connection = robot.ConnectionStatus;
            var assignment = await db.TaskAssignments.Include(a => a.Task).Include(a => a.Mission)
                .SingleOrDefaultAsync(a => a.RobotId == id && a.AssignmentStatus == "ACTIVE", ct);
            var ackTimeout = assignment?.Mission is { MissionStatus: "INITIALIZING" } initializing &&
                initializing.CreatedAt < ackCutoff && !await interventions.IsHeld(assignment.TaskId, ct);
            if (!offline && !ackTimeout && (robot.BatteryPercent == null || robot.BatteryPercent >= 20)) continue;
            var reason = offline ? "Robot heartbeat/telemetry timed out. Physical recovery is required before redispatching an active load."
                : ackTimeout ? "Robot did not acknowledge mission start before the configured timeout. Stop and physically confirm recovery."
                : critical ? "Battery below 10%. Motion must stop; Maintenance must recover the load before redispatch."
                : "Battery below 20%. New assignments are blocked.";
            if (offline) robot.ConnectionStatus = "OFFLINE";
            if ((offline || critical || ackTimeout) && assignment != null)
            {
                robot.OperationalStatus = "ERROR";
                // Keep ACTIVE ownership until Maintenance has physically accounted for the load.
                if (assignment.Mission is { MissionStatus: not ("COMPLETED" or "FAILED" or "ABORTED" or "CANCELLED") } mission)
                {
                    db.MissionStatusHistories.Add(new MissionStatusHistory { MissionId = mission.MissionId,
                        PreviousStatus = mission.MissionStatus, NewStatus = "FAILED", Source = "SYSTEM", Reason = reason, ChangedAt = now });
                    mission.MissionStatus = "FAILED"; mission.FailureReason = reason; mission.EndExecutionTime = now;
                    db.TaskStatusHistories.Add(new TaskStatusHistory { TaskId = assignment.TaskId,
                        PreviousStatus = assignment.Task.TaskStatus, NewStatus = "FAILED", Reason = reason, ChangedAt = now });
                    assignment.Task.TaskStatus = "FAILED"; assignment.Task.FailureReason = reason;
                }
            }
            if (connection != robot.ConnectionStatus || previous != robot.OperationalStatus)
                db.RobotStatusHistories.Add(new RobotStatusHistory { RobotId = id,
                    PreviousConnectionStatus = connection, NewConnectionStatus = robot.ConnectionStatus,
                    PreviousOperationalStatus = previous, NewOperationalStatus = robot.OperationalStatus,
                    Reason = reason, ChangedAt = now });
            robot.UpdatedAt = now;
            await OperationalAlertService.RaiseAsync(db, robot, offline ? "OFFLINE" : ackTimeout ? "START_ACK_TIMEOUT" : critical ? "CRITICAL_BATTERY" : "LOW_BATTERY",
                assignment != null && (offline || critical || ackTimeout) ? "ERROR" : "WARNING", reason, now,
                assignment?.TaskId, assignment?.Mission?.MissionId, ct);
            if ((offline || critical || ackTimeout) && assignment != null)
                await interventions.QueueSystemPauseAsync(assignment, reason, now, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            changed.Add(robot.RobotCode);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              db.ChangeTracker.Clear();
              logger?.LogError(ex, "Robot {Id} health check failed; checking remaining robots.", id);
          }
        }
        return changed;
    }
}

public sealed class RobotHealthWorker(IServiceScopeFactory scopes, FleetRealtimePublisher realtime,
    ILogger<RobotHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                foreach (var code in await scope.ServiceProvider.GetRequiredService<RobotHealthMonitor>()
                    .CheckAsync(DateTime.UtcNow, stoppingToken))
                    await realtime.RobotUpdatedAsync(code, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Robot health check failed; retrying on next tick."); }
        }
    }
}
