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
        public event EventHandler? Disconnected;

        public MqttService()
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
            _mqttClient.DisconnectedAsync += OnDisconnected;
        }

        private readonly string[] _brokers = { "broker.emqx.io", "broker.hivemq.com", "test.mosquitto.org" };

        public async Task ConnectAsync()
        {
            foreach (var broker in _brokers)
            {
                try
                {
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(broker, 1883)
                        .WithClientId($"MetalMonitor_{Guid.NewGuid():N}")
                        .WithCleanSession(true)
                        .WithTimeout(TimeSpan.FromSeconds(5))
                        .Build();

                    Console.WriteLine($"[MQTT] 尝试连接 {broker}...");
                    await _mqttClient.ConnectAsync(options);
                    Console.WriteLine($"[MQTT] 成功连接到 {broker}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MQTT] 连接 {broker} 失败: {ex.Message}");
                }
            }
            
            throw new InvalidOperationException("无法连接到任何MQTT Broker");
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

        private async Task OnDisconnected(MqttClientDisconnectedEventArgs e)
        {
            Console.WriteLine("[MQTT] 连接断开");
            Disconnected?.Invoke(this, EventArgs.Empty);
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