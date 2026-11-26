using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;

namespace SmartFleetBE.Services;

// Application ACKs are emitted only after mission progress commits to PostgreSQL.
public sealed class QrDemoProgressWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    QrDemoRouteService routes, MqttService publisher, FleetRealtimePublisher realtime,
    ILogger<QrDemoProgressWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += async args =>
        {
            try
            {
                if (args.ApplicationMessage.Payload.Length > 4096) return;
                var parts = args.ApplicationMessage.Topic.Split('/');
                if (parts.Length != 4 || parts[0] != "smartfleet" || parts[1] != "robot" || parts[3] != "qr-progress") return;
                var report = JsonSerializer.Deserialize<QrDemoRouteService.RouteProgressReport>(args.ApplicationMessage.ConvertPayloadToString(), Options);
                if (report == null || string.IsNullOrWhiteSpace(report.EventId) || report.EventId.Length > 100) return;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
                if (!await db.Robots.AnyAsync(r => r.RobotCode == parts[2] && r.IsActive, stoppingToken)) return;
                if (report.Event == "LOCALIZATION_CONFIG")
                {
                    await publisher.PublishAsync($"smartfleet/robot/{parts[2]}/localization-map",
                        JsonSerializer.Serialize(new { eventId = report.EventId, map = routes.GetLocalizationMap() }, Options), stoppingToken);
                    return;
                }
                ProgressDecision result;
                if (report.Event is "CHARGE_WAYPOINT" or "CHARGE_DOCK_STATUS" or "CHARGE_FAILED" or "CHARGE_STOPPED")
                    result = await scope.ServiceProvider.GetRequiredService<ChargingService>().Progress(parts[2], report, stoppingToken);
                else if (report.Event is "WAYPOINT_REACHED" or "VERIFICATION_STATUS")
                    result = await scope.ServiceProvider.GetRequiredService<MissionExecutionService>().ProgressAsync(parts[2], report, stoppingToken);
                else
                {
                    var localization = routes.ReceiveProgress(parts[2], report);
                    result = new(localization.Accepted, localization.Reason);
                }
                await publisher.PublishAsync($"smartfleet/robot/{parts[2]}/qr-progress-ack", JsonSerializer.Serialize(new
                { eventId = report.EventId, missionId = report.MissionId, accepted = result.Accepted,
                    reason = result.Reason, action = result.Action }, Options), stoppingToken);
                if (result.Accepted && report.Event == "WAYPOINT_REACHED")
                {
                    try
                    {
                        var assignment = await db.Missions.Where(m => m.MissionId == report.MissionId).Select(m => m.AssignmentId).SingleAsync(stoppingToken);
                        await realtime.MissionUpdatedAsync(assignment, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Progress committed; realtime broadcast failed"); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "QR progress failed; robot will remain stopped and retry the same event"); }
        };
        var options = MqttService.CreateOptions(configuration, $"SmartFleetBE-qr-progress-{Guid.NewGuid():N}");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(options, stoppingToken);
                    var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic("smartfleet/robot/+/qr-progress")
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build(), stoppingToken);
                    if (result.Items.Any(x => (int)x.ResultCode >= 128)) throw new IOException("QR subscription rejected");
                    logger.LogInformation("Persistent mission progress subscriber ready");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "QR progress worker reconnecting");
                if (client.IsConnected) try { await client.DisconnectAsync(); } catch (Exception) { }
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
