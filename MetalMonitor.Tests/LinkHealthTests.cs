using MetalMonitor.core.Services;
using MetalMonitor.Core;
using Moq;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// 链路健康单元测试。
    ///
    /// 覆盖：
    ///   1. 纯函数五态判定（假时间，不 sleep）：Inactive/Recovering/Healthy/NoValidFrame/NoSignal
    ///   2. DeviceService 事件接线：连接 → Recovering、无字节超期 → NoSignal、
    ///      收到有效帧 → Healthy、断开 → Inactive
    ///   3. 跃迁去重：同一状态重复评估不重复广播（避免每秒心跳造成事件风暴）
    ///   4. Reconnecting 第六态：断开且重连循环运行时返回 Reconnecting
    ///
    /// 时间约定：评估器与 EvaluateLinkHealthAt 均接收单调毫秒（Environment.TickCount64），
    /// 因此测试可以直接喂"未来时间"，无需真实等待看门狗周期。
    /// </summary>
    public class LinkHealthTests : IDisposable
    {
        private readonly string _tempDir;

        /// <summary>docs/protocol/PROTOCOL.md v0.9 标准示例帧（NODE_ID=0x00，12 字节，CRC 正确）。</summary>
        private static readonly byte[] ValidFrame =
        {
            0xAA, 0x55, 0x00, 0x06, 0x01, 0xFD, 0x00, 0xE2, 0x0C, 0x01, 0x2E, 0xF8
        };

        public LinkHealthTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetalMonitorLinkTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // 构造带串口能力的服务：数据源固定为"串口"，空设备列表（本文件只验证链路判据）
        private DeviceService CreateService(Mock<ISerialService>? serial = null, ConfigService? config = null)
        {
            config ??= new ConfigService(Path.Combine(_tempDir, "config.json")) { DataSource = "串口" };

            var mqtt = new Mock<IMqttService>();
            mqtt.SetupGet(x => x.IsConnected).Returns(false);

            var db = new Mock<IDatabaseService>();
            db.Setup(x => x.GetAllDevicesAsync()).ReturnsAsync(new List<Device>());
            db.Setup(x => x.GetAllMetricConfigsAsync()).ReturnsAsync(new List<MetricConfig>());
            db.Setup(x => x.AddDataRecordsBatchAsync(It.IsAny<List<DataRecord>>())).Returns(Task.CompletedTask);
            db.Setup(x => x.CheckpointAsync()).Returns(Task.CompletedTask);

            return new DeviceService(new AlarmService(), mqtt.Object, db.Object,
                serial?.Object ?? new Mock<ISerialService>().Object, new FrameParser(), config);
        }

        // ═══════════════════════════════════════════════════════════
        // 纯函数五态判定
        // ═══════════════════════════════════════════════════════════
        [Theory]
        // 未连接（connectedAt=0）→ Inactive
        [InlineData(10000, 0, 5000, 5000, LinkHealth.Inactive)]
        // 刚连上 1 秒、还没收到字节 → Handshaking
        [InlineData(2000, 1000, 0, 0, LinkHealth.Handshaking)]
        // 边界：恰好等于看门狗周期（3 秒）仍属等待窗口
        [InlineData(4000, 1000, 0, 0, LinkHealth.Handshaking)]
        // 超过看门狗周期仍无字节 → NoSignal
        [InlineData(4001, 1000, 0, 0, LinkHealth.NoSignal)]
        // 有字节但从未见过有效帧 → NoValidFrame
        [InlineData(5000, 1000, 4500, 0, LinkHealth.NoValidFrame)]
        // 有效帧新鲜 → Healthy
        [InlineData(5000, 1000, 4500, 4800, LinkHealth.Healthy)]
        // 字节新鲜但有效帧超期 → NoValidFrame（乱码/协议不匹配）
        [InlineData(9000, 1000, 8500, 2000, LinkHealth.NoValidFrame)]
        // 字节也超期 → NoSignal（线断/对端无输出）
        [InlineData(9000, 1000, 5000, 2000, LinkHealth.NoSignal)]
        public void Evaluate_ReturnsExpectedState(long now, long connectedAt, long lastByte, long lastValidFrame, LinkHealth expected)
        {
            var actual = LinkHealthEvaluator.Evaluate(now, connectedAt, lastByte, lastValidFrame, 3);

            Assert.Equal(expected, actual);
        }

        // ═══════════════════════════════════════════════════════════
        // DeviceService 事件接线
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void ConnectionEstablished_TransitionsToHandshaking_AndBroadcastsOnce()
        {
            var serial = new Mock<ISerialService>();
            var svc = CreateService(serial);
            var changes = new List<LinkHealthChangedEventArgs>();
            svc.LinkHealthChanged += (s, e) => changes.Add(e);

            serial.Raise(x => x.ConnectionStateChanged += null, new object(), true);

            Assert.Equal(LinkHealth.Handshaking, svc.LinkHealth);
            Assert.Single(changes);
            Assert.Equal(LinkHealth.Handshaking, changes[0].Health);

            // 同一状态重复评估不得重复广播
            svc.EvaluateLinkHealthAt(Environment.TickCount64 + 100);
            Assert.Single(changes);
        }

        [Fact]
        public void ConnectedWithoutBytes_AfterWatchdog_TransitionsToNoSignal()
        {
            var serial = new Mock<ISerialService>();
            var svc = CreateService(serial);
            var changes = new List<LinkHealthChangedEventArgs>();
            svc.LinkHealthChanged += (s, e) => changes.Add(e);

            serial.Raise(x => x.ConnectionStateChanged += null, new object(), true);
            long connectedAt = Environment.TickCount64;

            // 假时间越过 3 秒看门狗窗口
            svc.EvaluateLinkHealthAt(connectedAt + 10_000);

            Assert.Equal(LinkHealth.NoSignal, svc.LinkHealth);
            Assert.Equal(LinkHealth.NoSignal, changes[changes.Count - 1].Health);
            Assert.Contains("未收到任何串口数据", changes[changes.Count - 1].Reason);
        }

        [Fact]
        public void ValidFrame_AfterConnection_TransitionsToHealthy()
        {
            var serial = new Mock<ISerialService>();
            var svc = CreateService(serial);
            var changes = new List<LinkHealthChangedEventArgs>();
            svc.LinkHealthChanged += (s, e) => changes.Add(e);

            serial.Raise(x => x.ConnectionStateChanged += null, new object(), true);
            serial.Raise(x => x.DataReceived += null, new SerialRawDataEventArgs(ValidFrame));

            svc.EvaluateLinkHealthAt(Environment.TickCount64 + 100);

            Assert.Equal(LinkHealth.Healthy, svc.LinkHealth);
            Assert.Equal(LinkHealth.Healthy, changes[changes.Count - 1].Health);
            Assert.Null(changes[changes.Count - 1].Reason);
        }

        [Fact]
        public void Disconnect_TransitionsToInactive()
        {
            var serial = new Mock<ISerialService>();
            var svc = CreateService(serial);
            var changes = new List<LinkHealthChangedEventArgs>();
            svc.LinkHealthChanged += (s, e) => changes.Add(e);

            serial.Raise(x => x.ConnectionStateChanged += null, new object(), true);
            serial.Raise(x => x.ConnectionStateChanged += null, new object(), false);

            Assert.Equal(LinkHealth.Inactive, svc.LinkHealth);
            Assert.Equal(LinkHealth.Inactive, changes[changes.Count - 1].Health);
            Assert.Equal("串口未连接", changes[changes.Count - 1].Reason);
        }

        // ═══════════════════════════════════════════════════════════
        // Reconnecting 第六态
        // ═══════════════════════════════════════════════════════════
        [Theory]
        // 断开且重连循环在跑 → Reconnecting
        [InlineData(10000, 0, 5000, 5000, true, LinkHealth.Reconnecting)]
        // 断开但无重连循环 → Inactive（默认参数）
        [InlineData(10000, 0, 5000, 5000, false, LinkHealth.Inactive)]
        // 已连接状态不受 reconnecting 参数影响
        [InlineData(5000, 1000, 4500, 4800, true, LinkHealth.Healthy)]
        [InlineData(5000, 1000, 4500, 4800, false, LinkHealth.Healthy)]
        public void Evaluate_WithReconnectingFlag_ReturnsExpected(long now, long connectedAt, long lastByte, long lastValidFrame, bool reconnecting, LinkHealth expected)
        {
            var actual = LinkHealthEvaluator.Evaluate(now, connectedAt, lastByte, lastValidFrame, 3, reconnecting);
            Assert.Equal(expected, actual);
        }

        // ═══════════════════════════════════════════════════════════
        // 启动后从未连上 + 记忆端口列表非空 + 设备插在未记录端口
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// 验证该组合场景下重连循环能够启动。
        ///
        /// 场景条件：
        ///   · LinkHealth 为 Inactive（本次启动从未连上），不满足理由一；
        ///   · 记忆端口列表仅含 COM3，当前在场端口为 COM5，SerialPortAppeared 返回 false，不满足原理由二。
        /// 判据放宽后应启动重连循环，可观察信号为 DataSourceStateChanged 携带"检测到串口设备接入"。
        /// </summary>
        [Fact]
        public async Task Reconnect_StartsWhenRememberedPortsAbsentButAnotherPortPresent()
        {
            var serial = new Mock<ISerialService>();
            serial.SetupGet(x => x.IsAvailable).Returns(true);
            serial.SetupGet(x => x.IsConnected).Returns(false);
            serial.Setup(x => x.GetAvailablePorts()).Returns(new[] { "COM5" });   // 在场的口本子上没记过
            serial.Setup(x => x.ConnectAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            serial.Setup(x => x.DisconnectAsync()).Returns(Task.CompletedTask);

            var config = new ConfigService(Path.Combine(_tempDir, "config-window-c.json")) { DataSource = "串口" };
            config.RecordSerialPortSuccess("COM3");   // 记忆端口为 COM3，当前不在场

            var svc = CreateService(serial, config);
            var states = new List<DataSourceStateChangedEventArgs>();
            svc.DataSourceStateChanged += (s, e) => states.Add(e);

            try
            {
                await svc.UpdateDevicesAsync();   // 第一拍心跳即进入 TryStartReconnect
            }
            finally
            {
                svc.Dispose();                    // 叫停后台重连循环，避免测试泄漏
            }

            Assert.Contains(states, e => e.State == DataSourceState.Searching);
            Assert.Equal(LinkHealth.Inactive, svc.LinkHealth);   // 尚未打开端口，由后台重连循环后续更新
        }
    }
}
