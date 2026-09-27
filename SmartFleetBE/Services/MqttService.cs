using MQTTnet;

namespace SmartFleetBE.Services
{
    public class MqttService
    {
        private readonly IMqttClient _mqttClient;

        public MqttService()
        {
            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();
        }

        public async Task PublishAsync(
            string topic,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (!_mqttClient.IsConnected)
            {
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer("localhost", 1883)
                    .WithClientId($"SmartFleetBE-{Guid.NewGuid()}")
                    .Build();

                await _mqttClient.ConnectAsync(options, cancellationToken);
            }

            var mqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(message)
                .Build();

            await _mqttClient.PublishAsync(mqttMessage, cancellationToken);
        }
    }
}