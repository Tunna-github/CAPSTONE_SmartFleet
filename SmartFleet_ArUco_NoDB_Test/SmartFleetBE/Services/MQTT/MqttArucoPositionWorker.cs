using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.Hubs;

namespace SmartFleetBE.Services;

// DEMO: Subscribes to existing robot ArUco MQTT messages.
// No DbContext, no SQL tables. Does not send motion/mission commands.
public sealed class MqttArucoPositionWorker(
    IConfiguration configuration,
    IHubContext<FleetHub> hub,
    ILogger<MqttArucoPositionWorker> logger) : BackgroundService
{
    private static readonly MapDemoStore demo = MapDemoStore.Shared;

    public sealed class ArucoScanMessage
    {
        [JsonPropertyName("arucoId")]
        public int? ArucoId { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args => ReceiveAsync(args, stoppingToken);
        var options = MqttService.CreateOptions(configuration,
            $"SmartFleetBE-aruco-memory-{Guid.NewGuid():N}");

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
                                .WithTopic("smartfleet/robot/+/aruco")
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .Build(), stoppingToken);

                    if (result.Items.Any(i => (int)i.ResultCode >= 128))
                        throw new IOException("ArUco MQTT subscribe was rejected.");

                    logger.LogInformation("ArUco NO-DB MAP DEMO subscriber ready (R01).");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ArUco demo MQTT connection failed; retrying.");
                if (client.IsConnected)
                {
                    try { await client.DisconnectAsync(); }
                    catch (Exception disconnectEx)
                    {
                        logger.LogDebug(disconnectEx, "Demo MQTT disconnect failed.");
                    }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ReceiveAsync(
        MqttApplicationMessageReceivedEventArgs args, CancellationToken ct)
    {
        try
        {
            var parts = args.ApplicationMessage.Topic.Split('/');
            if (parts.Length != 4 || parts[0] != "smartfleet" ||
                parts[1] != "robot" || parts[3] != "aruco" ||
                string.IsNullOrWhiteSpace(parts[2]) ||
                args.ApplicationMessage.Payload.Length > 512)
                return;

            var message = JsonSerializer.Deserialize<ArucoScanMessage>(
                args.ApplicationMessage.ConvertPayloadToString());
            if (message?.ArucoId is not int markerId ||
                markerId < 0 || markerId > 9999)
                return;

            var position = demo.ReceiveScan(parts[2], markerId);
            if (position == null)
            {
                logger.LogInformation(
                    "DEMO MAP ignored {RobotCode} ArUco {ArucoId}: robot not R01 or ID not mapped",
                    parts[2], markerId);
                return;
            }

            // SignalR serializes record property names to camelCase for the React map.
            await hub.Clients.All.SendAsync("RobotPositionUpdated", position, ct);
            logger.LogInformation("DEMO MAP: {Robot} saw ArUco {Id} at {Cell}",
                position.RobotCode, markerId, position.CellCode);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Bad ArUco MQTT JSON payload.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ArUco demo map receive error.");
        }
    }
}
