
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace SmartFleetBE.Services;

// Separate MQTT connections for commands and fast status ACKs.
public sealed class MqttService(IConfiguration configuration) : IDisposable
{
    private readonly IMqttClient _commandClient =
        new MqttClientFactory().CreateMqttClient();

    private readonly IMqttClient _statusAckClient =
        new MqttClientFactory().CreateMqttClient();

    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _statusAckGate = new(1, 1);

    private readonly string _commandClientId =
        $"SmartFleetBE-command-publisher-{Guid.NewGuid():N}";

    private readonly string _statusAckClientId =
        $"SmartFleetBE-status-ack-{Guid.NewGuid():N}";

    public static MqttClientOptions CreateOptions(
        IConfiguration configuration,
        string clientId,
        bool persistent = false)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(
                configuration["Mqtt:Host"] ?? "localhost",
                configuration.GetValue("Mqtt:Port", 1883))
            .WithClientId(clientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(!persistent);

        if (!string.IsNullOrEmpty(configuration["Mqtt:Username"]))
        {
            builder.WithCredentials(
                configuration["Mqtt:Username"],
                configuration["Mqtt:Password"]);
        }

        return builder.Build();
    }

    // Normal MQTT publishing, including robot commands.
    public async Task PublishAsync(
        string topic,
        string message,
        CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            if (!_commandClient.IsConnected)
            {
                await _commandClient.ConnectAsync(
                    CreateOptions(configuration, _commandClientId),
                    timeout.Token);
            }

            var result = await _commandClient.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(message)
                    .WithQualityOfServiceLevel(
                        MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build(),
                timeout.Token);

            if (!result.IsSuccess)
            {
                throw new IOException(
                    $"MQTT command publish failed: {result.ReasonCode}");
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    // Connect the ACK publisher before robot status events arrive.
    public async Task WarmStatusAckConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        await _statusAckGate.WaitAsync(cancellationToken);

        try
        {
            if (_statusAckClient.IsConnected)
            {
                return;
            }

            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await _statusAckClient.ConnectAsync(
                CreateOptions(configuration, _statusAckClientId),
                timeout.Token);
        }
        finally
        {
            _statusAckGate.Release();
        }
    }

    // Fast ACK path: independent of robot command publishing.
    public async Task PublishStatusAckAsync(
        string topic,
        string message,
        CancellationToken cancellationToken = default)
    {
        await _statusAckGate.WaitAsync(cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            // Reconnect only when unexpectedly disconnected.
            if (!_statusAckClient.IsConnected)
            {
                await _statusAckClient.ConnectAsync(
                    CreateOptions(configuration, _statusAckClientId),
                    timeout.Token);
            }

            var result = await _statusAckClient.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(message)
                    .WithQualityOfServiceLevel(
                        MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build(),
                timeout.Token);

            if (!result.IsSuccess)
            {
                throw new IOException(
                    $"MQTT status ACK publish failed: {result.ReasonCode}");
            }
        }
        finally
        {
            _statusAckGate.Release();
        }
    }

    public void Dispose()
    {
        _commandClient.Dispose();
        _statusAckClient.Dispose();
        _commandGate.Dispose();
        _statusAckGate.Dispose();
    }
}
