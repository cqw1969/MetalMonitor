using MQTTnet;
using MQTTnet.Client;
using System.Text;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    /// <summary>
    /// MQTT 远程数据源。连接策略：依次尝试三个公共 Broker（EMQX→HiveMQ→Mosquitto），
    /// 单个 5 秒超时，全部失败抛异常由上层回退模拟数据。
    /// 注意：MessageReceived/Disconnected 事件由 MQTTnet 内部线程触发（非 UI 线程）。
    /// </summary>
    public class MqttService : IMqttService, IDisposable
    {
        /// <summary>底层 MQTTnet 客户端实例，承载连接、订阅、发布与事件回调。</summary>
        private readonly IMqttClient _mqttClient;
        /// <summary>对象释放标记，防止 Dispose 重复释放底层客户端。</summary>
        private bool _disposed = false;

        /// <summary>获取当前是否已与 Broker 建立连接。</summary>
        public bool IsConnected => _mqttClient.IsConnected;

        /// <summary>
        /// 收到订阅消息事件；由 MQTTnet 内部后台线程触发（非 UI 线程），
        /// 事件参数携带消息主题与 UTF-8 解码后的字符串负载。
        /// </summary>
        public event EventHandler<MqttMessageReceivedEventArgs>? MessageReceived;
        /// <summary>连接断开事件（网络异常或被 Broker 踢出时触发），在 MQTTnet 后台线程上回调。</summary>
        public event EventHandler? Disconnected;

        /// <summary>
        /// 创建 MQTT 客户端并挂接消息接收与断线回调；构造时不发起任何网络连接。
        /// </summary>
        public MqttService()
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
            _mqttClient.DisconnectedAsync += OnDisconnected;
        }

        /// <summary>候选 Broker 地址列表，按 EMQX → HiveMQ → Mosquitto 的顺序依次尝试连接。</summary>
        private readonly string[] _brokers = { "broker.emqx.io", "broker.hivemq.com", "test.mosquitto.org" };

        /// <summary>
        /// 依次尝试连接候选 Broker，首个连接成功者立即返回。
        /// </summary>
        /// <returns>表示异步连接操作的任务。</returns>
        /// <remarks>
        /// 每个 Broker 使用随机 ClientId、CleanSession，连接超时 5 秒、端口 1883；
        /// 单个 Broker 失败仅记录控制台输出并继续尝试下一个；
        /// 全部失败时抛出 <see cref="InvalidOperationException"/>，由上层回退模拟数据。
        /// </remarks>
        /// <exception cref="InvalidOperationException">所有候选 Broker 均无法连接时抛出。</exception>
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

        /// <summary>
        /// 断开与当前 Broker 的连接；未连接时直接返回，不做任何操作。
        /// </summary>
        /// <returns>表示异步断开操作的任务。</returns>
        public async Task DisconnectAsync()
        {
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
            }
        }

        /// <summary>
        /// 向当前 Broker 订阅指定主题（支持通配符，如 metalmonitor/devices/#）。
        /// </summary>
        /// <param name="topic">MQTT 主题过滤器。</param>
        /// <returns>表示异步订阅操作的任务。</returns>
        public async Task SubscribeAsync(string topic)
        {
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .Build();

            await _mqttClient.SubscribeAsync(topicFilter);
        }

        /// <summary>
        /// 向指定主题发布一条文本消息（QoS1 至少一次、不保留）。
        /// </summary>
        /// <param name="topic">目标 MQTT 主题。</param>
        /// <param name="payload">消息文本，按 UTF-8 编码为字节负载。</param>
        /// <returns>表示异步发布操作的任务。</returns>
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

        // MQTTnet 内部线程回调：仅把字节流转成字符串原样广播，不做业务解析（职责单一）
        /// <summary>
        /// MQTTnet 消息接收回调：将消息负载按 UTF-8 解码为字符串后原样广播给订阅者。
        /// </summary>
        /// <param name="e">MQTTnet 消息事件参数，包含主题与字节负载。</param>
        /// <returns>已完成的任务（本回调为同步处理，仅满足异步事件签名）。</returns>
        /// <remarks>运行在 MQTTnet 内部后台线程；本方法不做任何业务解析，解析与异常隔离由上层负责。</remarks>
        private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.PayloadSegment.ToArray();
            var payloadString = Encoding.UTF8.GetString(payload);
            
            Console.WriteLine($"[MQTT] 收到消息: {topic} -> {payloadString}");
            MessageReceived?.Invoke(this, new MqttMessageReceivedEventArgs(topic, payloadString));
            await Task.CompletedTask;
        }

        // 断线回调（网络异常/Broker 踢出时触发）：广播给上层，由上层决定重连策略
        /// <summary>
        /// MQTTnet 断线回调：记录日志并向上层广播 <see cref="Disconnected"/> 事件。
        /// </summary>
        /// <param name="e">MQTTnet 断线事件参数。</param>
        /// <returns>已完成的任务（本回调为同步处理，仅满足异步事件签名）。</returns>
        /// <remarks>是否重连、何时回退模拟数据由上层决定，本方法不发起重连。</remarks>
        private async Task OnDisconnected(MqttClientDisconnectedEventArgs e)
        {
            Console.WriteLine("[MQTT] 连接断开");
            Disconnected?.Invoke(this, EventArgs.Empty);
            await Task.CompletedTask;
        }

        /// <summary>释放底层 MQTT 客户端等托管资源，并请求 GC 不再调用终结器。</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源的统一实现（标准 Dispose 模式），重复调用安全。
        /// </summary>
        /// <param name="disposing">true 表示由 Dispose 调用，可释放托管资源；false 表示由终结器调用，仅清理非托管资源。</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _mqttClient?.Dispose();
            }

            _disposed = true;
        }

        /// <summary>终结器：未显式 Dispose 时兜底释放，按非托管释放路径调用 <see cref="Dispose(bool)"/>。</summary>
        ~MqttService()
        {
            Dispose(false);
        }
    }
}