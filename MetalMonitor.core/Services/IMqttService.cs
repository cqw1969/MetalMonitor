using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    public interface IMqttService
    {
        Task ConnectAsync();
        Task DisconnectAsync();
        Task SubscribeAsync(string topic);
        Task PublishAsync(string topic, string payload);
        event EventHandler<MqttMessageReceivedEventArgs>? MessageReceived;
        bool IsConnected { get; }
    }

    public class MqttMessageReceivedEventArgs : EventArgs
    {
        public string Topic { get; }
        public string Payload { get; }

        public MqttMessageReceivedEventArgs(string topic, string payload)
        {
            Topic = topic;
            Payload = payload;
        }
    }
}