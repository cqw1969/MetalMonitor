using MetalMonitor.core.Services;
using MetalMonitor.Core;
using Moq;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// DeviceService 批量写入缓冲与数据源调度单元测试
    ///
    /// 核心规则（对应需求 2.1(5)）：
    ///   - 数据记录先进入内存缓冲 _pendingBatch；
    ///   - 达到 BatchSize（config.json 配置，默认 50）才批量落盘；
    ///   - 切换数据源/配置、系统退出时立即 flush 剩余数据；
    ///   - MQTT 连接激活时不生成模拟数据；MQTT 消息同样进入缓冲。
    /// 用 Moq 构造 IDatabaseService 的替身，捕获每次批量写入的内容。
    /// </summary>
    public class DeviceServiceBufferTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly Mock<IDatabaseService> _db = new();
        private readonly AlarmService _alarm = new();
        private readonly System.Collections.Generic.List<System.Collections.Generic.List<DataRecord>> _batches = new();

        public DeviceServiceBufferTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetalMonitorSvcTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);

            // 捕获所有批量写入调用（复制一份，避免被测代码清空列表影响断言）
            _db.Setup(x => x.AddDataRecordsBatchAsync(It.IsAny<System.Collections.Generic.List<DataRecord>>()))
               .Callback<System.Collections.Generic.List<DataRecord>>(b =>
                   _batches.Add(new System.Collections.Generic.List<DataRecord>(b)))
               .Returns(Task.CompletedTask);
            _db.Setup(x => x.CheckpointAsync()).Returns(Task.CompletedTask);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // 构造测试用指标配置与设备
        private static System.Collections.Generic.List<MetricConfig> DefaultMetrics() => new()
        {
            new MetricConfig
            {
                Name = "Temperature", DisplayName = "温度", Unit = "℃",
                AlarmThreshold = 85, AlarmEnabled = true,
                AlarmDirection = AlarmDirection.Above,
                MinValue = 0, MaxValue = 150, IsActive = true
            }
        };

        private static System.Collections.Generic.List<Device> Devices(params Device[] devices) => devices.ToList();

        // 创建服务：1 台活动设备 + 1 个活动指标，BatchSize=5（每 tick 产生 1 条记录）
        private DeviceService CreateService(out ConfigService config, params Device[] devices)
        {
            _db.Setup(x => x.GetAllDevicesAsync()).ReturnsAsync(Devices(devices));
            _db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(DefaultMetrics());

            var configPath = Path.Combine(_tempDir, "config.json");
            config = new ConfigService(configPath) { BatchSize = 5 };

            return new DeviceService(_alarm, _db.Object, config);
        }

        private static async Task Tick(DeviceService svc, int times = 1)
        {
            for (int i = 0; i < times; i++)
                await svc.UpdateDevicesAsync();
        }

        [Fact]
        public async Task FirstTick_LoadsDevicesAndConfigs_BufferBelowThresholdDoesNotWrite()
        {
            var svc = CreateService(out _, new Device("1号冲压机") { Id = "d1" });

            await Tick(svc);

            Assert.Single(svc.GetDevices());
            Assert.Single(svc.GetMetricConfigs());
            Assert.Empty(_batches); // 仅 1 条缓冲，未达 5 条阈值
        }

        [Fact]
        public async Task Buffer_ReachesBatchSize_FlushesExactlyOnceWithAllRecords()
        {
            var svc = CreateService(out _, new Device("1号冲压机") { Id = "d1" });

            await Tick(svc, 5); // 每 tick 1 条 → 第 5 tick 达到阈值，落盘 5 条

            Assert.Single(_batches);
            Assert.Equal(5, _batches[0].Count);

            await Tick(svc); // 第 6 tick 又产生 1 条，未达阈值
            Assert.Single(_batches);
        }

        [Fact]
        public async Task FlushAsync_WritesRemainingBuffer_ThenBecomesNoOp()
        {
            var svc = CreateService(out _, new Device("1号冲压机") { Id = "d1" });

            await Tick(svc, 3); // 3 条缓冲
            await svc.FlushAsync();

            Assert.Single(_batches);
            Assert.Equal(3, _batches[0].Count);

            // 缓冲已空，再次 flush 不应产生写入
            await svc.FlushAsync();
            Assert.Single(_batches);
        }

        [Fact]
        public async Task Dispose_FlushesRemainingBuffer()
        {
            var svc = CreateService(out _, new Device("1号冲压机") { Id = "d1" });

            await Tick(svc, 2); // 2 条缓冲
            svc.Dispose();      // 退出时必须落盘

            Assert.Single(_batches);
            Assert.Equal(2, _batches[0].Count);
        }

        [Fact]
        public async Task SetDataSourceAsync_FlushesPendingBufferBeforeSwitch()
        {
            var svc = CreateService(out _, new Device("1号冲压机") { Id = "d1" });

            await Tick(svc, 3); // 3 条缓冲
            await svc.SetDataSourceAsync("模拟数据");

            Assert.Single(_batches);
            Assert.Equal(3, _batches[0].Count);
        }

        [Fact]
        public async Task FlushedRecords_CarryCorrectDeviceMetricUnitAndValue()
        {
            var svc = CreateService(out _, new Device("1号冲压机") { Id = "d1" });

            await Tick(svc, 5);

            var record = _batches[0][0];
            Assert.Equal("d1", record.DeviceId);
            Assert.Equal("1号冲压机", record.DeviceName);
            Assert.Equal("Temperature", record.MetricName);
            Assert.Equal("℃", record.Unit);
            Assert.InRange(record.Value, 0, 150);
        }

        [Fact]
        public async Task InactiveDevice_GeneratesNoRecords()
        {
            var svc = CreateService(out _,
                new Device("1号冲压机") { Id = "d1", IsActive = true },
                new Device("停用机") { Id = "d2", IsActive = false });

            await Tick(svc, 5);

            Assert.Equal(5, _batches[0].Count);
            Assert.All(_batches[0], r => Assert.Equal("d1", r.DeviceId));
        }

        [Fact]
        public async Task MqttActive_SkipsSimulatedDataGeneration()
        {
            _db.Setup(x => x.GetAllDevicesAsync()).ReturnsAsync(Devices(new Device("1号冲压机") { Id = "d1" }));
            _db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(DefaultMetrics());

            var mqtt = new Mock<IMqttService>();
            mqtt.SetupGet(x => x.IsConnected).Returns(true);
            mqtt.Setup(x => x.ConnectAsync()).Returns(Task.CompletedTask);
            mqtt.Setup(x => x.SubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

            var svc = new DeviceService(_alarm, mqtt.Object, _db.Object);
            await svc.SetDataSourceAsync("MQTT");

            await Tick(svc, 3);

            Assert.Empty(_batches); // MQTT 模式下不生成模拟数据，缓冲为空
        }

        [Fact]
        public async Task MqttMessage_WithMultiMetrics_EnqueuesAllMetricsAndUpdatesDevice()
        {
            _db.Setup(x => x.GetAllDevicesAsync())
               .ReturnsAsync(Devices(new Device("1号冲压机") { Id = "d1" }));
            _db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(DefaultMetrics());

            var mqtt = new Mock<IMqttService>();
            mqtt.SetupGet(x => x.IsConnected).Returns(true); // 已连接：切源不真正发起连接，仅启用 MQTT 通路

            var svc = new DeviceService(_alarm, mqtt.Object, _db.Object);
            await svc.ReloadDevicesAsync(); // 先加载设备列表
            await svc.SetDataSourceAsync("MQTT"); // 启用 MQTT：心跳不再生成模拟数据

            // 模拟发布端推送一条含两个指标的消息
            var payload = JsonSerializer.Serialize(new
            {
                Id = "device-001",
                Name = "1号冲压机",
                Metrics = new System.Collections.Generic.Dictionary<string, double>
                {
                    ["Temperature"] = 92.5,
                    ["Pressure"] = 12.3
                }
            });
            mqtt.Raise(x => x.MessageReceived += null,
                new MqttMessageReceivedEventArgs("metalmonitor/devices/device-001/data", payload));

            // 改造后接入层只写暂存区与落库缓冲：设备值在心跳排空暂存区时统一回写
            await svc.UpdateDevicesAsync();
            await svc.FlushAsync();

            Assert.Single(_batches);
            Assert.Equal(2, _batches[0].Count);
            Assert.Contains(_batches[0], r => r.MetricName == "Pressure" && r.Value == 12.3);
            Assert.Contains(_batches[0], r => r.MetricName == "Temperature" && r.Value == 92.5);

            // 设备对象的值也被更新
            var device = svc.GetDevices().First(d => d.Name == "1号冲压机");
            Assert.Equal(92.5, device.GetMetricValue("Temperature"));
        }

        [Fact]
        public async Task MqttMessage_UnknownDevice_EnqueuesNothing()
        {
            _db.Setup(x => x.GetAllDevicesAsync())
               .ReturnsAsync(Devices(new Device("1号冲压机") { Id = "d1" }));
            _db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(DefaultMetrics());

            var mqtt = new Mock<IMqttService>();
            mqtt.SetupGet(x => x.IsConnected).Returns(false);

            var svc = new DeviceService(_alarm, mqtt.Object, _db.Object);
            await svc.ReloadDevicesAsync();

            var payload = JsonSerializer.Serialize(new
            {
                Id = "device-999",
                Name = "幽灵设备",
                Metrics = new System.Collections.Generic.Dictionary<string, double> { ["Temperature"] = 50 }
            });
            mqtt.Raise(x => x.MessageReceived += null,
                new MqttMessageReceivedEventArgs("metalmonitor/devices/device-999/data", payload));

            await svc.FlushAsync();

            Assert.Empty(_batches);
        }

        [Fact]
        public async Task DeviceRenamed_AfterReload_StillRoutedByNewName()
        {
            _db.Setup(x => x.GetAllDevicesAsync())
               .ReturnsAsync(Devices(new Device("1号冲压机") { Id = "d1" }));
            _db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(DefaultMetrics());

            var mqtt = new Mock<IMqttService>();
            mqtt.SetupGet(x => x.IsConnected).Returns(true);

            var svc = new DeviceService(_alarm, mqtt.Object, _db.Object);
            await svc.ReloadDevicesAsync();
            await svc.SetDataSourceAsync("MQTT");

            // 数据库侧把同一台设备改名（Id 不变），再触发一次增量同步——
            // 同步会原地改写 device.Name（保留对象引用），因此按名索引必须在此重建
            _db.Setup(x => x.GetAllDevicesAsync())
               .ReturnsAsync(Devices(new Device("2号冲压机") { Id = "d1" }));
            await svc.ReloadDevicesAsync();

            // D3 回归点：索引若未重建，改名后的消息会落进"设备不存在"分支而静默丢弃
            var payload = JsonSerializer.Serialize(new
            {
                Id = "device-001",
                Name = "2号冲压机",
                Metrics = new System.Collections.Generic.Dictionary<string, double> { ["Temperature"] = 61.5 }
            });
            mqtt.Raise(x => x.MessageReceived += null,
                new MqttMessageReceivedEventArgs("metalmonitor/devices/device-001/data", payload));

            await svc.UpdateDevicesAsync();
            await svc.FlushAsync();

            Assert.Equal(61.5, svc.GetDevices().Single().GetMetricValue("Temperature"));
            Assert.Contains(_batches, b => b.Any(r => r.MetricName == "Temperature" && r.Value == 61.5));
        }
    }
}
