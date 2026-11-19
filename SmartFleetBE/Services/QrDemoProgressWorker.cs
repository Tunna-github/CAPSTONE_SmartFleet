using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using MQTTnet;
using MQTTnet.Protocol;

namespace SmartFleetBE.Services;

// Dedicated MQTT topic so existing /status-ack and mission lifecycle are untouched.
// Used only for the single-robot Webots simulation, never as an emergency stop.
public sealed class QrDemoProgressWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    QrDemoRouteService routes,
    MqttService publisher,
    FleetRealtimePublisher realtime,
    ILogger<QrDemoProgressWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += async args =>
        {
            try
            {
                if (args.ApplicationMessage.Payload.Length > 4096) return;
                var parts = args.ApplicationMessage.Topic.Split('/');
                if (parts.Length != 4 || parts[0] != "smartfleet" || parts[1] != "robot" ||
                    parts[3] != "qr-progress" || string.IsNullOrWhiteSpace(parts[2])) return;
                var json = args.ApplicationMessage.ConvertPayloadToString();
                var report = JsonSerializer.Deserialize<QrDemoRouteService.RouteProgressReport>(json, Options);
                if (report == null || string.IsNullOrWhiteSpace(report.EventId)) return;
                var result = routes.ReceiveProgress(parts[2], report);
                // Acknowledge pickup location (no package verification): advance
                // the REAL Mission row from NAVIGATING_TO_PICKUP to NAVIGATING_TO_DELIVERY.
                if (result.Accepted && report.Event == "WAYPOINT_REACHED")
                {
                    var view = routes.GetMission(report.MissionId);
                    if (view != null && view.PickupReached && !view.DeliveryReached &&
                        report.WaypointIndex == view.PickupWaypointIndex)
                    {
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
                        var mission = await db.Missions.SingleOrDefaultAsync(
                            m => m.MissionId == report.MissionId &&
                                 m.Assignment.Robot.RobotCode == parts[2], stoppingToken);
                        if (mission is { MissionStatus: "NAVIGATING_TO_PICKUP" })
                        {
                            var now = DateTime.UtcNow;
                            mission.MissionStatus = "NAVIGATING_TO_DELIVERY";
                            mission.LastProgressAt = now;
                            db.MissionStatusHistories.Add(new MissionStatusHistory
                            {
                                MissionId = report.MissionId,
                                PreviousStatus = "NAVIGATING_TO_PICKUP",
                                NewStatus = "NAVIGATING_TO_DELIVERY",
                                Source = "WEBOTS_QR_ODOMETRY_DEMO",
                                Reason = $"Reached pickup QR{report.ArucoId}; pickup package verification not enabled.",
                                ChangedAt = now
                            });
                            await db.SaveChangesAsync(stoppingToken);
                            try { await realtime.MissionUpdatedAsync(mission.AssignmentId, stoppingToken); }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            { logger.LogWarning(ex, "Mission pickup SignalR broadcast failed"); }
                        }
                    }
                }
                var ack = JsonSerializer.Serialize(new
                {
                    eventId = report.EventId,
                    missionId = report.MissionId,
                    accepted = result.Accepted,
                    reason = result.Reason
                }, Options);
                await publisher.PublishAsync($"smartfleet/robot/{parts[2]}/qr-progress-ack", ack, stoppingToken);
            }
            catch (JsonException ex) { logger.LogWarning(ex, "Invalid QR progress JSON"); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "QR progress handling failed"); }
        };

        var options = MqttService.CreateOptions(
            configuration, $"SmartFleetBE-qr-progress-{Guid.NewGuid():N}");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(options, stoppingToken);
                    var result = await client.SubscribeAsync(
                        new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic("smartfleet/robot/+/qr-progress")
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .Build(), stoppingToken);
                    if (result.Items.Any(x => (int)x.ResultCode >= 128))
                        throw new IOException("QR progress subscription rejected by broker");
                    logger.LogInformation("Webots QR progress MQTT subscription ready");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "QR progress worker reconnecting");
                if (client.IsConnected)
                    try { await client.DisconnectAsync(); }
                    catch (Exception disconnectEx) { logger.LogDebug(disconnectEx, "Already disconnected"); }
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
