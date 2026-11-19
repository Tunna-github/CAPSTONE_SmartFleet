using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Services;

public sealed class RobotCommandOutboxWorker(IServiceScopeFactory scopes, MqttService mqtt,
    IConfiguration configuration, QrDemoRouteService qrDemo,
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
                    var active = await db.Missions.AnyAsync(m => m.AssignmentId == command.AssignmentId &&
                        m.MissionStatus == "INITIALIZING" && m.Assignment.AssignmentStatus == "ACTIVE", stoppingToken);

                    if (active) 
                    {
                        // The existing CIRCLE command becomes a real QR route only
                        // when this Webots pilot is explicitly enabled.
                        // NEVER send a CIRCLE fallback for an unconfigured task.
                        try
                        {
                            string payload = command.Payload;
                            if (configuration.GetValue<bool>("DemoRoute:Enabled"))
                            {
                                var assignment = await db.TaskAssignments.AsNoTracking()
                                    .Include(a => a.Robot).Include(a => a.Mission)
                                    .SingleAsync(a => a.AssignmentId == command.AssignmentId, stoppingToken);
                                payload = qrDemo.BuildMqttCommand(assignment);
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
