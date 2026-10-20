using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    /// <summary>
    /// MQTT 通信契约：连接管理、主题订阅/发布与消息/断线事件。
    /// </summary>
    /// <remarks>实现支持多 Broker 自动切换（EMQX/HiveMQ/Mosquitto，单个超时 5 秒切下一个）；
    /// 消息回调运行在 MQTTnet 后台线程，订阅方不得在回调中直接操作 UI 控件。</remarks>
    public interface IMqttService
    {
        /// <summary>连接 Broker（按内置列表依次尝试，全部失败抛异常）</summary>
        Task ConnectAsync();

        /// <summary>断开当前 Broker 连接并清理订阅</summary>
        Task DisconnectAsync();

        /// <summary>
        /// 订阅主题。
        /// </summary>
        /// <param name="topic">MQTT 主题过滤器（支持通配符，如 metalmonitor/devices/#）</param>
        Task SubscribeAsync(string topic);

        /// <summary>
        /// 向指定主题发布字符串载荷。
        /// </summary>
        /// <param name="topic">目标主题</param>
        /// <param name="payload">消息内容（通常为 JSON）</param>
        Task PublishAsync(string topic, string payload);

        /// <summary>消息到达事件（后台线程触发）；载荷为原始字符串，由订阅方解析</summary>
        event EventHandler<MqttMessageReceivedEventArgs>? MessageReceived;

        /// <summary>连接断开事件（后台线程触发）；上层据此回退模拟数据</summary>
        event EventHandler? Disconnected;

        /// <summary>当前是否处于已连接状态</summary>
        bool IsConnected { get; }
    }

    /// <summary>
    /// MQTT 消息到达事件参数：携带消息主题与字符串载荷。
    /// </summary>
    public class MqttMessageReceivedEventArgs : EventArgs
    {
        /// <summary>消息来源主题</summary>
        public string Topic { get; }

        /// <summary>消息原始载荷（DeviceService 按 JSON 反序列化）</summary>
        public string Payload { get; }

        /// <summary>
        /// 构造消息事件参数。
        /// </summary>
        /// <param name="topic">消息主题</param>
        /// <param name="payload">消息载荷</param>
        public MqttMessageReceivedEventArgs(string topic, string payload)
        {
            Topic = topic;
            Payload = payload;
        }
    }
}
