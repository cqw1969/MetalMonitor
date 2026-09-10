using MQTTnet;
using MQTTnet.Client;
using System.Text;
using System.Text.Json;

namespace MetalMonitor.MqttPublisher
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var devices = new List<DeviceData>
            {
                new DeviceData { Id = "device-001", Name = "1号冲压机" },
                new DeviceData { Id = "device-002", Name = "2号CNC" },
                new DeviceData { Id = "device-003", Name = "3号磨床" }
            };

            var factory = new MqttFactory();
            var mqttClient = factory.CreateMqttClient();

            var brokers = new[] { "broker.emqx.io", "broker.hivemq.com", "test.mosquitto.org" };
            bool connected = false;
            
            foreach (var broker in brokers)
            {
                try
                {
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(broker, 1883)
                        .WithClientId($"MqttPublisher_{Guid.NewGuid():N}")
                        .WithCleanSession(true)
                        .WithTimeout(TimeSpan.FromSeconds(5))
                        .Build();

                    Console.WriteLine($"尝试连接 {broker}...");
                    await mqttClient.ConnectAsync(options);
                    Console.WriteLine($"已连接到 {broker}");
                    connected = true;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"连接 {broker} 失败: {ex.Message}");
                }
            }
            
            if (!connected)
            {
                Console.WriteLine("无法连接到任何MQTT Broker，按任意键退出...");
                Console.ReadKey();
                return;
            }

            var random = new Random();
            var timer = new System.Timers.Timer(1000);

            timer.Elapsed += async (sender, e) =>
            {
                foreach (var device in devices)
                {
                    device.Metrics = GenerateMetrics(random);

                    var payload = JsonSerializer.Serialize(device);
                    var topic = $"metalmonitor/devices/{device.Id}/data";

                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(Encoding.UTF8.GetBytes(payload))
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(false)
                        .Build();

                    await mqttClient.PublishAsync(message);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 发布: {topic} -> {payload}");
                }
            };

            timer.Start();

            Console.WriteLine("按任意键退出...");
            Console.ReadKey();

            timer.Stop();
            await mqttClient.DisconnectAsync();
        }

        // 生成与系统默认指标配置匹配的模拟值（6 类指标，范围覆盖报警阈值）
        private static Dictionary<string, double> GenerateMetrics(Random random)
        {
            return new Dictionary<string, double>
            {
                ["Temperature"] = Math.Round(55 + random.NextDouble() * 40, 1),    // 55~95（阈值 85，偶发报警）
                ["Voltage"] = Math.Round(340 + random.NextDouble() * 80, 1),       // 340~420（阈值 380）
                ["ProductionCount"] = Math.Round(random.NextDouble() * 5000, 1),
                ["Humidity"] = Math.Round(30 + random.NextDouble() * 70, 1),       // 30~100（阈值 90）
                ["Pressure"] = Math.Round(5 + random.NextDouble() * 12, 1),        // 5~17（阈值 10）
                ["Speed"] = Math.Round(2000 + random.NextDouble() * 2500, 1)       // 2000~4500（阈值 3000）
            };
        }

        class DeviceData
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public Dictionary<string, double> Metrics { get; set; } = new();
        }
    }
}