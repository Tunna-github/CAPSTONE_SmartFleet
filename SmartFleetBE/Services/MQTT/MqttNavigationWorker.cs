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
                parts[3] != "navigation" || string.IsNullOrWhiteSpace(parts[2]))
                return;

            var evt = JsonSerializer.Deserialize<RobotNavigationEvent>(
                args.ApplicationMessage.ConvertPayloadToString(), Options);
            if (evt == null) return;

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

                var result = coordinator.OnNavigationEvent(robot.RobotId, robot.RobotCode, evt);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Rejected {Event} for {Robot} at QR{Marker}: {Reason}",
                        evt.Event, robot.RobotCode, evt.IntersectionArUcoId, result.Error);
                    // Rejected navigation is always a local HOLD/STOP case for the robot.
                    return;
                }

                await NavigationMqttDispatch.PublishAsync(publisher, result.Directives, ct);
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
}
