
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.DTOs;

namespace SmartFleetBE.Services;

public sealed class MqttMissionStatusWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    MqttService publisher,
    ILogger<MqttMissionStatusWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private CancellationToken _stoppingToken;
    private bool _databaseWarmed;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += ReceiveAsync;

        var options = MqttService.CreateOptions(
            configuration,
            configuration["Mqtt:StatusClientId"]
                ?? "SmartFleetBE-mission-status",
            persistent: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Step 1: connect the dedicated ACK publisher.
                await publisher.WarmStatusAckConnectionAsync(
                    stoppingToken);

                // Step 2: warm up EF Core and Aiven connection.
                if (!_databaseWarmed)
                {
                    using var scope = scopes.CreateScope();

                    var db = scope.ServiceProvider
                        .GetRequiredService<SmartFleetDbContext>();

                    using var timeout = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken);

                    timeout.CancelAfter(TimeSpan.FromSeconds(10));

                    if (!await db.Database.CanConnectAsync(
                            timeout.Token))
                    {
                        throw new IOException(
                            "Aiven PostgreSQL is not ready for mission events.");
                    }

                    _databaseWarmed = true;

                    logger.LogInformation(
                        "Aiven PostgreSQL pre-warmed for mission events.");
                }

                // Step 3: subscribe to robot status messages.
                if (!client.IsConnected)
                {
                    using var timeout = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken);

                    timeout.CancelAfter(TimeSpan.FromSeconds(10));

                    await client.ConnectAsync(
                        options,
                        timeout.Token);

                    var result = await client.SubscribeAsync(
                        new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f
                                .WithTopic("smartfleet/robot/+/status")
                                .WithQualityOfServiceLevel(
                                    MqttQualityOfServiceLevel.AtLeastOnce))
                            .Build(),
                        timeout.Token);

                    if (result.Items.Any(
                            i => (int)i.ResultCode >= 128))
                    {
                        throw new IOException(
                            "MQTT broker rejected mission status subscription.");
                    }

                    logger.LogInformation(
                        "Mission status subscription ready; " +
                        "dedicated ACK publisher already connected.");
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(3),
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "MQTT status/ACK startup or connection failed; retrying.");

                if (client.IsConnected)
                {
                    try
                    {
                        await client.DisconnectAsync();
                    }
                    catch (Exception disconnectError)
                    {
                        logger.LogDebug(
                            disconnectError,
                            "MQTT status client already disconnected.");
                    }
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(3),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ReceiveAsync(
        MqttApplicationMessageReceivedEventArgs args)
    {
        var totalTimer = Stopwatch.StartNew();

        try
        {
            var topic = args.ApplicationMessage.Topic.Split('/');

            if (topic.Length != 4 ||
                topic[0] != "smartfleet" ||
                topic[1] != "robot" ||
                topic[3] != "status" ||
                string.IsNullOrWhiteSpace(topic[2]))
            {
                return;
            }

            if (args.ApplicationMessage.Payload.Length > 16384)
            {
                return;
            }

            var message =
                JsonSerializer.Deserialize<RobotMissionEvent>(
                    args.ApplicationMessage.ConvertPayloadToString(),
                    JsonOptions);

            if (message is null)
            {
                return;
            }

            // Database processing must finish before Applied ACK.
            using var scope = scopes.CreateScope();

            var dbTimer = Stopwatch.StartNew();

            var result = await scope.ServiceProvider
                .GetRequiredService<MissionLifecycleService>()
                .HandleAsync(
                    topic[2],
                    message,
                    _stoppingToken);

            dbTimer.Stop();

            long ackMilliseconds = 0;

            if (!string.IsNullOrWhiteSpace(message.EventId) &&
                message.EventId.Length <= 100)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    eventId = message.EventId,
                    assignmentId = message.AssignmentId,
                    accepted = result != MissionEventResult.Rejected,
                    result = result.ToString()
                });

                var ackTimer = Stopwatch.StartNew();

                await publisher.PublishStatusAckAsync(
                    $"smartfleet/robot/{topic[2]}/status-ack",
                    payload,
                    _stoppingToken);

                ackTimer.Stop();

                ackMilliseconds = ackTimer.ElapsedMilliseconds;
            }
            else
            {
                logger.LogWarning(
                    "Robot {RobotCode} sent a status event without a valid " +
                    "eventId; no application ACK can be correlated.",
                    topic[2]);
            }

            // Measure the exact time spent by the backend.
            logger.LogInformation(
                "Mission ACK timing | Robot={RobotCode} " +
                "TaskId={TaskId} AssignmentId={AssignmentId} " +
                "EventId={EventId} Event={Event} Result={Result} " +
                "DbMs={DbMs} AckPublishMs={AckPublishMs} TotalMs={TotalMs}",
                topic[2],
                message.TaskId,
                message.AssignmentId,
                message.EventId,
                message.Event,
                result,
                dbTimer.ElapsedMilliseconds,
                ackMilliseconds,
                totalTimer.ElapsedMilliseconds);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Ignored malformed robot status JSON.");
        }
        catch (OperationCanceledException)
            when (_stoppingToken.IsCancellationRequested)
        {
            args.ProcessingFailed = true;
        }
        catch (Exception ex)
        {
            args.ProcessingFailed = true;

            logger.LogError(
                ex,
                "Mission status processing/ACK failed after {Ms} ms; " +
                "robot must retry the same eventId until application ACK.",
                totalTimer.ElapsedMilliseconds);
        }
    }
}
