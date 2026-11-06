using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.DTOs;

namespace SmartFleetBE.Services;

// Separate MQTT subscriber; does not delay fast mission ACK.
public sealed class MqttRobotTelemetryWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    FleetRealtimePublisher realtime,
    ILogger<MqttRobotTelemetryWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args => ReceiveAsync(args, stoppingToken);
        var options = MqttService.CreateOptions(configuration,
            $"SmartFleetBE-telemetry-{Guid.NewGuid():N}");

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
                                .WithTopic("smartfleet/robot/+/telemetry")
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .WithTopicFilter(f => f
                                .WithTopic("smartfleet/robot/+/lidar")
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce))
                            .Build(), stoppingToken);
                    if (result.Items.Any(i => (int)i.ResultCode >= 128))
                        throw new IOException("MQTT telemetry subscription rejected.");
                    logger.LogInformation("MQTT telemetry subscriber ready.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT telemetry connection failed; retrying.");
                if (client.IsConnected)
                {
                    try { await client.DisconnectAsync(); }
                    catch (Exception disconnectEx)
                    {
                        logger.LogDebug(disconnectEx, "MQTT telemetry disconnected.");
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
            var topic = args.ApplicationMessage.Topic.Split('/');
            if (topic.Length == 4 && topic[0] == "smartfleet" &&
                topic[1] == "robot" && topic[3] == "lidar")
            {
                await ReceiveLidarAsync(topic[2], args, ct);
                return;
            }
            if (topic.Length != 4 || topic[0] != "smartfleet" ||
                topic[1] != "robot" || topic[3] != "telemetry" ||
                string.IsNullOrWhiteSpace(topic[2]) ||
                args.ApplicationMessage.Payload.Length > 4096)
                return;

            var message = JsonSerializer.Deserialize<RobotTelemetryEvent>(
                args.ApplicationMessage.ConvertPayloadToString(), JsonOptions);
            if (message == null || !Valid(message))
            {
                logger.LogWarning("Invalid telemetry from {RobotCode}", topic[2]);
                return;
            }

            var now = DateTime.UtcNow;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
            var robot = await db.Robots.SingleOrDefaultAsync(
                r => r.RobotCode == topic[2] && r.IsActive, ct);
            if (robot == null) return;

            robot.ConnectionStatus = "ONLINE";
            robot.LastHeartbeatTime = now;
            robot.LastTelemetryTime = now;
            robot.UpdatedAt = now;
            if (message.BatteryPercent.HasValue) robot.BatteryPercent = message.BatteryPercent.Value;
            if (message.BatteryVoltage.HasValue) robot.BatteryVoltage = message.BatteryVoltage.Value;
            if (message.PositionX.HasValue) robot.CurrentCoordX = message.PositionX.Value;
            if (message.PositionY.HasValue) robot.CurrentCoordY = message.PositionY.Value;
            if (message.Heading.HasValue) robot.CurrentHeading = message.Heading.Value;

            if (configuration.GetValue<bool>("Mqtt:StoreTelemetryLogs"))
                db.RobotTelemetryLogs.Add(new RobotTelemetryLog {
                    RobotId = robot.RobotId,
                    PositionX = message.PositionX, PositionY = message.PositionY,
                    Heading = message.Heading,
                    LinearVelocity = message.LinearVelocity,
                    AngularVelocity = message.AngularVelocity,
                    BatteryPercentage = message.BatteryPercent,
                    BatteryVoltage = message.BatteryVoltage,
                    CpuTemperature = message.CpuTemperature,
                    WifiSignalDbm = message.WifiSignalDbm,
                    RecordedAt = now
                });

            await db.SaveChangesAsync(ct);
            try
            {
                // Battery telemetry is persisted before broadcasting; no MQTT ACK is delayed.
                if (message.BatteryPercent.HasValue || message.BatteryVoltage.HasValue)
                {
                    int staleAfterSeconds = Math.Clamp(
                        configuration.GetValue("Battery:StaleAfterSeconds", 45), 10, 3600);

                    await realtime.BatteryUpdatedAsync(
                        RobotBatteryResponse.FromRobot(robot, now, staleAfterSeconds), ct);
                }

                await realtime.RobotUpdatedAsync(robot.RobotCode, ct);
                await realtime.TelemetryUpdatedAsync(robot.RobotCode, message, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Telemetry persisted, but SignalR broadcast failed.");
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid telemetry JSON.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telemetry processing failed.");
        }
    }

    private async Task ReceiveLidarAsync(string robotCode,
        MqttApplicationMessageReceivedEventArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(robotCode) ||
            args.ApplicationMessage.Payload.Length > 1024)
            return;

        RobotLidarEvent? lidar;
        try
        {
            lidar = JsonSerializer.Deserialize<RobotLidarEvent>(
                args.ApplicationMessage.ConvertPayloadToString(), JsonOptions);
        }
        catch (JsonException)
        {
            logger.LogWarning("Malformed LiDAR payload from {RobotCode}", robotCode);
            return;
        }

        if (lidar == null) return;
        var status = lidar.Status?.Trim().ToUpperInvariant();
        if (status is not ("CLEAR" or "DETECTED" or "CRITICAL" or "NO_SCAN_DATA"))
            return;

        // Reject inconsistent thresholds, NaN/overflow and impossible measurements.
        var distance = lidar.DistanceCm;
        bool valid = status switch
        {
            "CLEAR" or "NO_SCAN_DATA" => distance == null,
            "DETECTED" => distance is > 25m and <= 50m,
            "CRITICAL" => distance is >= 0m and <= 25m,
            _ => false
        };
        if (!valid) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        var robot = await db.Robots.AsNoTracking()
            .Where(r => r.RobotCode == robotCode && r.IsActive)
            .Select(r => new { r.RobotId, r.RobotCode })
            .SingleOrDefaultAsync(ct);
        if (robot == null) return;

        // Live sensor display only; does not mark robot online or touch DB rows.
        await realtime.LidarUpdatedAsync(robot.RobotId, robot.RobotCode,
            status, distance, DateTime.UtcNow, ct);
    }

    private static bool Range(decimal? v, decimal min, decimal max)
        => !v.HasValue || (v.Value >= min && v.Value <= max);

    private static bool Valid(RobotTelemetryEvent x)
        => Range(x.BatteryPercent, 0, 100) &&
           Range(x.BatteryVoltage, 0, 999) &&
           Range(x.PositionX, -9999999, 9999999) &&
           Range(x.PositionY, -9999999, 9999999) &&
           Range(x.Heading, -9999, 9999) &&
           Range(x.LinearVelocity, -9999, 9999) &&
           Range(x.AngularVelocity, -9999, 9999) &&
           Range(x.CpuTemperature, -999, 999) &&
           Range(x.WifiSignalDbm, -999, 999);
}
