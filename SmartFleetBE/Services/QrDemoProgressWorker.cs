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

    // SmartFleet QR transaction retry: same source of SQLSTATE 40001 as mission updates.
    private static bool IsPgConflict(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
            if (e is Npgsql.PostgresException pg &&
                (pg.SqlState == "40001" || pg.SqlState == "40P01"))
                return true;
        return false;
    }

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
                if (report.Event == "LOCALIZATION_CONFIG")
                {
                    await publisher.PublishAsync($"smartfleet/robot/{parts[2]}/localization-map",
                        JsonSerializer.Serialize(new { eventId = report.EventId, map = routes.GetLocalizationMap() }, Options), stoppingToken);
                    return;
                }
                if (report.Event == "WAYPOINT_REACHED")
                {
                    using var guardScope = scopes.CreateScope();
                    var guardDb = guardScope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
                    var held = await guardDb.TaskInterventions.AnyAsync(c =>
                        guardDb.Missions.Any(m => m.MissionId == report.MissionId && m.Assignment.TaskId == c.TaskId) &&
                        (c.Paused || c.State != "NORMAL"), stoppingToken);
                    if (held)
                    {
                        await publisher.PublishAsync($"smartfleet/robot/{parts[2]}/qr-progress-ack",
                            JsonSerializer.Serialize(new { eventId = report.EventId, accepted = false,
                                reason = "Task has a safety intervention." }), stoppingToken);
                        return;
                    }
                }
                var result = routes.ReceiveProgress(parts[2], report);
                // Acknowledge pickup location (no package verification): advance
                // the REAL Mission row from NAVIGATING_TO_PICKUP to NAVIGATING_TO_DELIVERY.
                if (result.Accepted && report.Event == "WAYPOINT_REACHED")
                {
                    var view = routes.GetMission(report.MissionId);
                    if (view != null && view.PickupReached && !view.DeliveryReached &&
                        report.WaypointIndex == view.PickupWaypointIndex)
                    {
                        long? changedAssignmentId = null;
                        const int maxQrAttempts = 5;
                        for (var attempt = 1; ; attempt++)
                        {
                            try
                            {
                                // Retry the WHOLE database operation with a FRESH scope.
                                // The in-memory waypoint report is idempotent, and the
                                // status guard prevents duplicate history inserts.
                                using var scope = scopes.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
                                await using var transaction = await db.Database.BeginTransactionAsync(
                                    System.Data.IsolationLevel.Serializable, stoppingToken);
                                var mission = await db.Missions.Include(m => m.Assignment).SingleOrDefaultAsync(
                                    m => m.MissionId == report.MissionId &&
                                         m.Assignment.Robot.RobotCode == parts[2], stoppingToken);
                                if (mission is { MissionStatus: "NAVIGATING_TO_PICKUP" } &&
                                    !await db.TaskInterventions.AnyAsync(c => c.TaskId == mission.Assignment.TaskId &&
                                        (c.Paused || c.State != "NORMAL"), stoppingToken))
                                {
                                    var now = DateTime.UtcNow;
                                    mission.MissionStatus = "NAVIGATING_TO_DELIVERY";
                                    mission.LastProgressAt = now;
                                    db.MissionStatusHistories.Add(new MissionStatusHistory
                                    {
                                        MissionId = report.MissionId,
                                        PreviousStatus = "NAVIGATING_TO_PICKUP",
                                        NewStatus = "NAVIGATING_TO_DELIVERY",
                                        Source = "ROBOT_AGENT",
                                        Reason = $"Reached pickup QR{report.ArucoId}; pickup package verification not enabled.",
                                        ChangedAt = now
                                    });
                                    await db.SaveChangesAsync(stoppingToken);
                                    changedAssignmentId = mission.AssignmentId;
                                }
                                await transaction.CommitAsync(stoppingToken);
                                break;
                            }
                            catch (Exception ex) when (IsPgConflict(ex) && attempt < maxQrAttempts)
                            {
                                logger.LogWarning(
                                    "QR pickup progress for mission {MissionId} hit a PostgreSQL conflict; " +
                                    "retrying the whole operation ({Attempt}/{MaxAttempts}).",
                                    report.MissionId, attempt, maxQrAttempts);
                                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 150), stoppingToken);
                            }
                        }
                        if (changedAssignmentId.HasValue)
                        {
                            try { await realtime.MissionUpdatedAsync(changedAssignmentId.Value, stoppingToken); }
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
