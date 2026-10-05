using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace SmartFleetBE.Services;

public sealed class MqttService(IConfiguration configuration) : IDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _clientId = $"SmartFleetBE-publisher-{Guid.NewGuid():N}";

    public static MqttClientOptions CreateOptions(IConfiguration configuration, string clientId, bool persistent = false)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(configuration["Mqtt:Host"] ?? "localhost", configuration.GetValue("Mqtt:Port", 1883))
            .WithClientId(clientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(!persistent);
        if (!string.IsNullOrEmpty(configuration["Mqtt:Username"]))
            builder.WithCredentials(configuration["Mqtt:Username"], configuration["Mqtt:Password"]);
        return builder.Build();
    }

    public async Task PublishAsync(string topic, string message, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (!_client.IsConnected)
                await _client.ConnectAsync(CreateOptions(configuration, _clientId), timeout.Token);
            var result = await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(message)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false).Build(), timeout.Token);
            if (!result.IsSuccess) throw new IOException($"MQTT publish failed: {result.ReasonCode}");
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { _client.Dispose(); _gate.Dispose(); }
}
