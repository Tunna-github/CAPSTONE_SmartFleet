using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.DTOs;

namespace SmartFleetBE.Services;

public sealed class MqttMissionStatusWorker(
    IServiceScopeFactory scopes, IConfiguration configuration, MqttService publisher,
    ILogger<MqttMissionStatusWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += ReceiveAsync;
        var options = MqttService.CreateOptions(configuration,
            configuration["Mqtt:StatusClientId"] ?? "SmartFleetBE-mission-status", persistent: true);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await client.ConnectAsync(options, timeout.Token);
                    var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic("smartfleet/robot/+/status")
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(), timeout.Token);
                    if (result.Items.Any(i => (int)i.ResultCode >= 128))
                        throw new IOException("MQTT broker rejected mission status subscription.");
                    logger.LogInformation("Subscribed to robot mission status.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT status connection failed; retrying.");
                if (client.IsConnected)
                {
                    try { await client.DisconnectAsync(); }
                    catch (Exception disconnectError) { logger.LogDebug(disconnectError, "MQTT connection already unavailable."); }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task ReceiveAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var topic = args.ApplicationMessage.Topic.Split('/');
            if (topic.Length != 4 || topic[0] != "smartfleet" || topic[1] != "robot" ||
                topic[3] != "status" || string.IsNullOrWhiteSpace(topic[2])) return;
            if (args.ApplicationMessage.Payload.Length > 16384) return;
            var message = JsonSerializer.Deserialize<RobotMissionEvent>(args.ApplicationMessage.ConvertPayloadToString(), JsonOptions);
            if (message is null) return;
            using var scope = scopes.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<MissionLifecycleService>()
                .HandleAsync(topic[2], message, _stoppingToken);
            logger.LogInformation("Robot {RobotCode} assignment {AssignmentId}: {Event} {Result}",
                topic[2], message.AssignmentId, message.Event, result);
            // Application ACK means SQL committed, not merely broker delivery.
            if (!string.IsNullOrWhiteSpace(message.EventId) && message.EventId.Length <= 100)
                await publisher.PublishAsync($"smartfleet/robot/{topic[2]}/status-ack", JsonSerializer.Serialize(new {
                    eventId = message.EventId, assignmentId = message.AssignmentId,
                    accepted = result != MissionEventResult.Rejected, result = result.ToString()
                }), _stoppingToken);
        }
        catch (JsonException ex) { logger.LogWarning(ex, "Ignored malformed robot status JSON."); }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { args.ProcessingFailed = true; }
        catch (Exception ex)
        {
            args.ProcessingFailed = true;
            logger.LogError(ex, "Mission status processing failed; robot must retry until application ACK.");
        }
    }
}
