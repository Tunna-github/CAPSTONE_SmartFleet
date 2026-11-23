using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

/// <summary>
/// Separate navigation topic; mission /status and its fast application ACK remain unchanged.
/// The real robot must publish APPROACH before the physical stop line at an intersection.
/// </summary>
public sealed class MqttNavigationWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    NavigationCoordinator coordinator,
    MqttService publisher,
    ILogger<MqttNavigationWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _messageGate = new(1, 1);
    // SMARTFLEET_NAV_ACK_V1: dedupe accepted requests on QoS1 resend.
    private readonly Dictionary<string, (string Robot, string Kind)> _ackCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _ackOrder = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args => ReceiveAsync(args, stoppingToken);
        var options = MqttService.CreateOptions(
            configuration, $"SmartFleetBE-navigation-{Guid.NewGuid():N}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(options, stoppingToken);
                    var result = await client.SubscribeAsync(
                        new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f
                                .WithTopic("smartfleet/robot/+/navigation")
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .WithTopicFilter(f => f
                                .WithTopic("smartfleet/robot/+/scan")
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .Build(), stoppingToken);
                    if (result.Items.Any(x => (int)x.ResultCode >= 128))
                        throw new IOException("Navigation MQTT subscription rejected.");
                    logger.LogInformation("Navigation MQTT subscriber ready.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Navigation MQTT subscriber unavailable; retrying.");
                if (client.IsConnected)
                {
                    try { await client.DisconnectAsync(); }
                    catch (Exception disconnectError)
                    {
                        logger.LogDebug(disconnectError, "Navigation client disconnected.");
                    }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        _messageGate.Dispose();
    }

    private async Task ReceiveAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken ct)
    {
        try
        {
            if (args.ApplicationMessage.Payload.Length > 2048) return;
            var parts = args.ApplicationMessage.Topic.Split('/');
            if (parts.Length != 4 || parts[0] != "smartfleet" || parts[1] != "robot" ||
                (parts[3] != "navigation" && parts[3] != "scan") ||
                string.IsNullOrWhiteSpace(parts[2]))
                return;

            var payload = args.ApplicationMessage.ConvertPayloadToString();
            // Correlation ID is optional for older clients; pilot clients always send it.
            using var requestDocument = JsonDocument.Parse(payload);
            string? eventId = null;
            if (requestDocument.RootElement.ValueKind == JsonValueKind.Object &&
                requestDocument.RootElement.TryGetProperty("eventId", out var idProperty) &&
                idProperty.ValueKind == JsonValueKind.String)
                eventId = idProperty.GetString();
            if (eventId is { Length: > 128 }) return;
            var kind = parts[3] == "scan" ? "SCAN" : "";
            RobotNavigationEvent? evt = null;
            RobotArUcoScanReport? scan = null;
            if (parts[3] == "scan")
                scan = JsonSerializer.Deserialize<RobotArUcoScanReport>(payload, Options);
            else
                evt = JsonSerializer.Deserialize<RobotNavigationEvent>(payload, Options);
            if (evt == null && scan == null) return;
            if (scan == null) kind = evt!.Event.Trim().ToUpperInvariant();

            await _messageGate.WaitAsync(ct);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
                var robot = await db.Robots.AsNoTracking()
                    .Where(x => x.RobotCode == parts[2] && x.IsActive)
                    .Select(x => new { x.RobotId, x.RobotCode })
                    .SingleOrDefaultAsync(ct);
                if (robot == null) return;
                if (eventId != null && _ackCache.TryGetValue(eventId, out var old))
                {
                    bool sameRequest = string.Equals(old.Robot, robot.RobotCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(old.Kind, kind, StringComparison.Ordinal);
                    await PublishPilotAck(robot.RobotCode, kind, eventId, sameRequest,
                        sameRequest ? "Already applied" : "Event ID reused for different robot/event", ct);
                    return;
                }

                var result = scan != null
                    ? coordinator.OnScan(robot.RobotId, robot.RobotCode, scan)
                    : coordinator.OnNavigationEvent(robot.RobotId, robot.RobotCode, evt);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Rejected {EventType} for {Robot}: {Reason}",
                        scan != null ? "SCAN" : evt!.Event, robot.RobotCode, result.Error);
                    // Rejected navigation is always a local HOLD/STOP case for the robot.
                    await PublishPilotAck(robot.RobotCode, kind, eventId, false, result.Error, ct);
                    return;
                }

                await NavigationMqttDispatch.PublishAsync(publisher, result.Directives, ct);
                if (eventId != null)
                {
                    _ackCache[eventId] = (robot.RobotCode, kind);
                    _ackOrder.Enqueue(eventId);
                    while (_ackOrder.Count > 4096)
                        _ackCache.Remove(_ackOrder.Dequeue());
                }
                await PublishPilotAck(robot.RobotCode, kind, eventId, true, "Applied", ct);
            }
            finally
            {
                _messageGate.Release();
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid robot navigation event JSON.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Navigation event failed; robot must remain stopped without a fresh grant.");
        }
    }

    // SMARTFLEET_NAV_ACK_V1: only a Backend-applied navigation state is ACKed.
    private Task PublishPilotAck(string robotCode, string kind, string? eventId,
        bool accepted, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return Task.CompletedTask;
        return publisher.PublishAsync($"smartfleet/robot/{robotCode}/navigation-ack",
            JsonSerializer.Serialize(new { robotCode, kind, eventId, accepted, reason }, Options), ct);
    }
}
