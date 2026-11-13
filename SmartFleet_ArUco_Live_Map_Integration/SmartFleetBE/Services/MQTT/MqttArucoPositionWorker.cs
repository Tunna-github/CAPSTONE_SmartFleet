using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.Hubs;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

// New isolated MQTT subscriber. It never changes mission status or robot movement.
public sealed class MqttArucoPositionWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    IHubContext<FleetHub> hub,
    ILogger<MqttArucoPositionWorker> logger) : BackgroundService
{
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
            $"SmartFleetBE-aruco-map-{Guid.NewGuid():N}");

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
                        throw new IOException("ArUco MQTT subscription rejected.");
                    logger.LogInformation("ArUco position subscriber ready.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ArUco MQTT subscriber failed; retrying.");
                if (client.IsConnected)
                {
                    try { await client.DisconnectAsync(); }
                    catch (Exception disconnectEx)
                    {
                        logger.LogDebug(disconnectEx, "ArUco subscriber disconnected.");
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

    private async Task ReceiveAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken ct)
    {
        try
        {
            var parts = args.ApplicationMessage.Topic.Split('/');
            if (parts.Length != 4 || parts[0] != "smartfleet" || parts[1] != "robot" ||
                parts[3] != "aruco" || string.IsNullOrWhiteSpace(parts[2]) ||
                args.ApplicationMessage.Payload.Length > 512)
                return;

            var body = JsonSerializer.Deserialize<ArucoScanMessage>(
                args.ApplicationMessage.ConvertPayloadToString());
            if (body?.ArucoId is not int markerId || markerId < 0 || markerId > 9999)
            {
                logger.LogWarning("Rejected invalid ArUco scan from {Robot}", parts[2]);
                return;
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
            var robot = await db.Robots.AsNoTracking()
                .Where(r => r.IsActive && r.RobotCode == parts[2])
                .Select(r => new { r.RobotId, r.RobotCode, r.WarehouseId })
                .SingleOrDefaultAsync(ct);
            if (robot is null)
            {
                logger.LogWarning("ArUco scan from unknown/inactive robot {Robot}", parts[2]);
                return;
            }

            var marker = await db.ArucoMarkerMaps.AsNoTracking()
                .SingleOrDefaultAsync(m => m.WarehouseId == robot.WarehouseId && m.ArucoId == markerId, ct);
            if (marker is null)
            {
                logger.LogInformation("ArUco {Id} is not mapped in warehouse {Warehouse}",
                    markerId, robot.WarehouseId);
                return;
            }

            var now = DateTime.UtcNow; // Server receive time is the last fix time.
            var position = await db.RobotArucoPositions.SingleOrDefaultAsync(
                p => p.RobotId == robot.RobotId, ct);
            if (position is null)
            {
                db.RobotArucoPositions.Add(new RobotArucoPosition
                {
                    RobotId = robot.RobotId, ArucoId = markerId, SeenAt = now
                });
            }
            else
            {
                position.ArucoId = markerId;
                position.SeenAt = now;
            }
            await db.SaveChangesAsync(ct);

            // Event payload is compatible with WarehouseMapPage's listener.
            await hub.Clients.All.SendAsync("RobotPositionUpdated", new
            {
                robot.RobotId,
                robot.RobotCode,
                robot.WarehouseId,
                ArucoId = markerId,
                marker.CellCode,
                marker.GridColumn,
                marker.GridRow,
                SeenAt = now
            }, ct);
        }
        catch (JsonException)
        {
            logger.LogWarning("Invalid ArUco message JSON.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ArUco position processing failed.");
        }
    }
}
