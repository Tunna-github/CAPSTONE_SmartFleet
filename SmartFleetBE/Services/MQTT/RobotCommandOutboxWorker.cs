using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace SmartFleetBE.Services;

public sealed class RobotCommandOutboxWorker(IServiceScopeFactory scopes, MqttService mqtt,
    IConfiguration configuration,
    ILogger<RobotCommandOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
                var pending = await db.RobotCommandOutbox.Where(c => c.DeliveredAt == null)
                    .OrderBy(c => c.Id).ToListAsync(stoppingToken);
                foreach (var command in pending)
                {
                    using var document = JsonDocument.Parse(command.Payload);
                    if (document.RootElement.TryGetProperty("commandId", out var id))
                    {
                        var commandId = id.GetString();
                        var current = await db.TaskInterventions.AnyAsync(c => c.CommandId == commandId, stoppingToken);
                        if (current)
                            await mqtt.PublishAsync(command.Topic, command.Payload, stoppingToken);
                        else
                        {
                            command.DeliveredAt = DateTime.UtcNow; // Superseded control, never deliver stale motion.
                            await db.SaveChangesAsync(stoppingToken);
                        }
                        // Controls are retried until application acknowledgement, not broker delivery.
                        continue;
                    }
                    var held = await db.TaskInterventions.AnyAsync(c =>
                        db.TaskAssignments.Any(a => a.AssignmentId == command.AssignmentId && a.TaskId == c.TaskId) &&
                        (c.Paused || c.State != "NORMAL"), stoppingToken);
                    if (held) continue;
                    var active = await db.Missions.AnyAsync(m => m.AssignmentId == command.AssignmentId &&
                        m.MissionStatus == "INITIALIZING" && m.Assignment.AssignmentStatus == "ACTIVE", stoppingToken);

                    if (active) 
                    {
                        // Publish the frozen, committed route in the Webots adapter.
                        // Never rebuild a route from a changed robot pose or fall back to CIRCLE.
                        try
                        {
                            string payload = command.Payload;
                            if (configuration.GetValue<bool>("DemoRoute:Enabled"))
                            {
                                var assignment = await db.TaskAssignments.AsNoTracking()
                                    .Include(a => a.Robot).Include(a => a.Mission)
                                    .SingleAsync(a => a.AssignmentId == command.AssignmentId, stoppingToken);
                                var execution = await db.MissionExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.MissionId == assignment.Mission!.MissionId, stoppingToken);
                                if (execution == null) throw new InvalidOperationException("Mission has no persisted route. Recover/requeue this legacy assignment before executing.");
                                payload = execution.RoutePayloadJson;
                            }
                            await mqtt.PublishAsync(command.Topic, payload, stoppingToken);
                            command.DeliveredAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(stoppingToken);
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Fail closed. Keep this outbox entry pending and allow
                            // other valid mission commands through in the same tick.
                            logger.LogWarning(ex, "QR route not ready for assignment {AssignmentId}; command not sent", command.AssignmentId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Robot command delivery failed; pending commands will be retried."); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
