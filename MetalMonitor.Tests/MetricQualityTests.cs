using MetalMonitor.core.Services;
using MetalMonitor.Core;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// T4.1 指标质量码单元测试。
    ///
    /// 覆盖：
    ///   1. 写值瞬间置 Good（零延迟），同值质量跃迁不重复通知
    ///   2. 从未写入的指标按 Bad 解释
    ///   3. 纯函数三态：新鲜 Good / 链路正常但超期 Stale / 链路故障且超期 Bad
    ///   4. DeviceService 心跳降级扫描：健康链路超期 → Stale；链路故障超期 → Bad
    ///
    /// 时间约定：与 LinkHealthTests 相同，直接传给评估器/扫描方法假时间，不做真实等待。
    /// </summary>
    public class MetricQualityTests : IDisposable
    {
        private readonly string _tempDir;

        /// <summary>PROTOCOL.md v0.9 标准示例帧（用于把链路拉到 Healthy）。</summary>
        private static readonly byte[] ValidFrame =
        {
            0xAA, 0x55, 0x00, 0x06, 0x01, 0xFD, 0x00, 0xE2, 0x0C, 0x01, 0x2E, 0xF8
        };

        public MetricQualityTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetalMonitorQualityTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private static List<MetricConfig> DefaultMetrics() => new()
        {
            new MetricConfig
            {
                Name = "Temperature", DisplayName = "温度", Unit = "℃",
                AlarmThreshold = 85, AlarmEnabled = true,
                AlarmDirection = AlarmDirection.Above,
                MinValue = 0, MaxValue = 150, IsActive = true
            }
        };

        // 构造服务：一台设备 + 一个启用指标，数据源固定为串口
        private async Task<DeviceService> CreateLoadedServiceAsync(Mock<ISerialService>? serial, Device device)
        {
            var config = new ConfigService(Path.Combine(_tempDir, "config.json")) { DataSource = "串口" };

            var mqtt = new Mock<IMqttService>();
            mqtt.SetupGet(x => x.IsConnected).Returns(false);

            var db = new Mock<IDatabaseService>();
            db.Setup(x => x.GetAllDevicesAsync()).ReturnsAsync(new List<Device> { device });
            db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(DefaultMetrics());
            db.Setup(x => x.AddDataRecordsBatchAsync(It.IsAny<List<DataRecord>>())).Returns(Task.CompletedTask);
            db.Setup(x => x.CheckpointAsync()).Returns(Task.CompletedTask);

            var svc = new DeviceService(new AlarmService(), mqtt.Object, db.Object,
                serial?.Object ?? new Mock<ISerialService>().Object, new FrameParser(), config);

            await svc.UpdateDevicesAsync();   // 首次心跳：同步设备列表与指标配置
            return svc;
        }

        // ═══════════════════════════════════════════════════════════
        // Device 层：写值即 Good、同值不重复通知、未写过为 Bad
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void SetMetricValue_MarksGoodImmediately_AndOnlyNotifiesOnChange()
        {
            var device = new Device("1号冲压机");
            var notified = new List<MetricQuality>();
            device.MetricQualityChanged += (metric, quality) => notified.Add(quality);

            // 从未写过 → Bad
            Assert.Equal(MetricQuality.Bad, device.GetMetricQuality("Temperature"));

            device.SetMetricValue("Temperature", 25.3);
            Assert.Equal(MetricQuality.Good, device.GetMetricQuality("Temperature"));
            Assert.Single(notified);
            Assert.Equal(MetricQuality.Good, notified[0]);

            // 再次写值仍是 Good：质量未跃迁，不应再通知
            device.SetMetricValue("Temperature", 26.0);
            Assert.Single(notified);
        }

        [Fact]
        public void SetMetricQuality_SameValue_DoesNotNotify()
        {
            var device = new Device("1号冲压机");
            int count = 0;
            device.MetricQualityChanged += (metric, quality) => count++;

            device.SetMetricQuality("Temperature", MetricQuality.Stale);
            device.SetMetricQuality("Temperature", MetricQuality.Stale);

            Assert.Equal(1, count);
            Assert.Equal(MetricQuality.Stale, device.GetMetricQuality("Temperature"));
        }

        // ═══════════════════════════════════════════════════════════
        // 纯函数三态
        // ═══════════════════════════════════════════════════════════
        [Theory]
        [InlineData(10000, 0, false, MetricQuality.Bad)]        // 从未写入
        [InlineData(10000, 9000, false, MetricQuality.Good)]    // 1 秒前写入，阈值 3 秒
        [InlineData(20000, 9000, false, MetricQuality.Stale)]   // 超期且链路正常/未知
        [InlineData(20000, 9000, true, MetricQuality.Bad)]      // 超期且链路明确故障
        public void Evaluate_ReturnsExpectedQuality(long now, long lastValue, bool linkFaulted, MetricQuality expected)
        {
            var actual = MetricQualityEvaluator.Evaluate(now, lastValue, 3, linkFaulted);

            Assert.Equal(expected, actual);
        }

        // ═══════════════════════════════════════════════════════════
        // DeviceService 心跳降级扫描
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public async Task Scan_LinkHealthy_OverdueMetricBecomesStale()
        {
            var serial = new Mock<ISerialService>();
            var device = new Device("1号冲压机");
            var svc = await CreateLoadedServiceAsync(serial, device);

            // 把链路拉到 Healthy：连接 + 收到有效帧
            serial.Raise(x => x.ConnectionStateChanged += null, new object(), true);
            serial.Raise(x => x.DataReceived += null, new SerialRawDataEventArgs(ValidFrame));
            svc.EvaluateLinkHealthAt(Environment.TickCount64 + 100);
            Assert.Equal(LinkHealth.Healthy, svc.LinkHealth);

            device.SetMetricValue("Temperature", 25.3);
            long writtenAt = device.GetMetricValueMs("Temperature");

            // 假时间前进 4 秒（阈值 3 秒），链路仍健康 → 只标陈旧
            svc.ApplyQualityDegradationAt(writtenAt + 4000);

            Assert.Equal(MetricQuality.Stale, device.GetMetricQuality("Temperature"));
        }

        [Fact]
        public async Task Scan_LinkFaulted_OverdueMetricBecomesBad()
        {
            var serial = new Mock<ISerialService>();
            var device = new Device("1号冲压机");
            var svc = await CreateLoadedServiceAsync(serial, device);

            // 不连接串口 → LinkHealth=Inactive（明确故障态）
            Assert.Equal(LinkHealth.Inactive, svc.LinkHealth);

            device.SetMetricValue("Temperature", 25.3);
            long writtenAt = device.GetMetricValueMs("Temperature");

            svc.ApplyQualityDegradationAt(writtenAt + 4000);

            Assert.Equal(MetricQuality.Bad, device.GetMetricQuality("Temperature"));
        }
    }
}
