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
                new DeviceData { Id = "device-001", Name = "1号冲压机", Temperature = 65 },
                new DeviceData { Id = "device-002", Name = "2号CNC", Temperature = 60 },
                new DeviceData { Id = "device-003", Name = "3号磨床", Temperature = 70 }
            };

            var factory = new MqttFactory();
            var mqttClient = factory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("localhost", 1883)
                //.WithTcpServer("broker.emqx.io", 1883)
                .WithClientId("MqttPublisher")
                .WithCleanSession(true)
                .Build();

            await mqttClient.ConnectAsync(options);
            Console.WriteLine("已连接到 MQTT Broker");

            var random = new Random();
            var timer = new System.Timers.Timer(1000);

            timer.Elapsed += async (sender, e) =>
            {
                foreach (var device in devices)
                {
                    device.Temperature = Math.Round(55 + random.NextDouble() * 35, 1);
                    
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

        class DeviceData
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public double Temperature { get; set; }
        }
    }
}