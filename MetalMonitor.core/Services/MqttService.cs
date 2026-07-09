using MQTTnet;
using MQTTnet.Client;
using System.Text;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    public class MqttService : IMqttService, IDisposable
    {
        private readonly IMqttClient _mqttClient;
        private bool _disposed = false;

        public bool IsConnected => _mqttClient.IsConnected;

        public event EventHandler<MqttMessageReceivedEventArgs>? MessageReceived;

        public MqttService()
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        }

        public async Task ConnectAsync()
        {
            var options = new MqttClientOptionsBuilder()
                //这里是wsl2的当前IP地址，确保MQTT Broker在这个地址上运行
                //.WithTcpServer("172.22.159.39", 1883)
                .WithTcpServer("broker.emqx.io", 1883)
                .WithClientId($"MetalMonitor_{Guid.NewGuid():N}")
                .WithCleanSession(true)
                .Build();

            await _mqttClient.ConnectAsync(options);
        }

        public async Task DisconnectAsync()
        {
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
            }
        }

        public async Task SubscribeAsync(string topic)
        {
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .Build();

            await _mqttClient.SubscribeAsync(topicFilter);
        }

        public async Task PublishAsync(string topic, string payload)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(message);
        }

        private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.PayloadSegment.ToArray();
            var payloadString = Encoding.UTF8.GetString(payload);
            
            Console.WriteLine($"[MQTT] 收到消息: {topic} -> {payloadString}");
            MessageReceived?.Invoke(this, new MqttMessageReceivedEventArgs(topic, payloadString));
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _mqttClient?.Dispose();
            }

            _disposed = true;
        }

        ~MqttService()
        {
            Dispose(false);
        }
    }
}