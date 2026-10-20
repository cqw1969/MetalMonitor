using MetalMonitor.Core;
using System.IO;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// ConfigService（config.json 配置服务）单元测试
    /// 覆盖：缺失文件默认值、写后持久化、损坏文件回退、Reload 重新读取
    /// </summary>
    public class ConfigServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;

        public ConfigServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetalMonitorConfigTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "config.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void Ctor_MissingFile_CreatesWithDefaults()
        {
            var svc = new ConfigService(_configPath);

            Assert.True(File.Exists(_configPath));
            Assert.Equal(50, svc.BatchSize);
            Assert.Equal(1000, svc.RefreshInterval);
            Assert.Equal("模拟数据", svc.DataSource);
        }

        [Fact]
        public void Properties_Set_PersistAcrossInstances()
        {
            var svc = new ConfigService(_configPath)
            {
                BatchSize = 25,
                RefreshInterval = 500,
                DataSource = "MQTT",
                DatabasePath = @"D:\TestData"
            };

            // 重新实例化相当于重启程序，配置应从文件读回
            var reloaded = new ConfigService(_configPath);

            Assert.Equal(25, reloaded.BatchSize);
            Assert.Equal(500, reloaded.RefreshInterval);
            Assert.Equal("MQTT", reloaded.DataSource);
            Assert.Equal(@"D:\TestData", reloaded.DatabasePath);
        }

        [Fact]
        public void Ctor_CorruptJson_FallsBackToDefaultsWithoutThrowing()
        {
            File.WriteAllText(_configPath, "{ this is not valid json ]");

            var svc = new ConfigService(_configPath);

            Assert.Equal(50, svc.BatchSize);
            Assert.Equal("模拟数据", svc.DataSource);
        }

        [Fact]
        public void Reload_RereadsExternalFileChanges()
        {
            var svc = new ConfigService(_configPath) { BatchSize = 10 };

            // 模拟外部程序改写了配置文件
            File.WriteAllText(_configPath,
                "{\"DatabasePath\":\"\",\"BatchSize\":77,\"RefreshInterval\":1000,\"DataSource\":\"模拟数据\"}");

            svc.Reload();

            Assert.Equal(77, svc.BatchSize);
        }

        // ===== T3.4 第④步：Serial 段（端口持久化 + NODE_ID 路由表）=====

        /// <summary>
        /// 串口名写后持久化：跨实例（模拟重启）读回一致。
        /// </summary>
        [Fact]
        public void SerialPortName_Set_PersistsAcrossInstances()
        {
            var svc = new ConfigService(_configPath) { SerialPortName = "COM7" };

            var reloaded = new ConfigService(_configPath);

            Assert.Equal("COM7", reloaded.SerialPortName);
        }

        /// <summary>
        /// 旧版 config.json 无 Serial 段（向后兼容）：端口为空、路由表为空，不抛异常。
        /// </summary>
        [Fact]
        public void SerialSection_MissingInOldConfig_FallsBackToEmpty()
        {
            File.WriteAllText(_configPath,
                "{\"DatabasePath\":\"\",\"BatchSize\":50,\"RefreshInterval\":1000,\"DataSource\":\"MQTT\"}");

            var svc = new ConfigService(_configPath);

            Assert.Equal(string.Empty, svc.SerialPortName);
            Assert.Empty(svc.GetNodeRoutingSnapshot());
        }

        /// <summary>
        /// 路由表解析：十六进制键（大小写均可）解析为字节键；非法键（"GG"、带 0x 前缀）静默忽略。
        /// </summary>
        [Fact]
        public void NodeRouting_HexKeysParsed_InvalidKeysIgnored()
        {
            File.WriteAllText(_configPath,
                "{\"Serial\":{\"PortName\":\"\",\"NodeRouting\":{\"2A\":\"一号炉\",\"0f\":\"二号炉\",\"GG\":\"坏键\",\"0x0F\":\"带前缀坏键\"}}}");

            var svc = new ConfigService(_configPath);
            var routing = svc.GetNodeRoutingSnapshot();

            Assert.Equal(2, routing.Count); // "2A"→0x2A、"0f"→0x0F；两个坏键被忽略
            Assert.Equal("一号炉", routing[0x2A]);
            Assert.Equal("二号炉", routing[0x0F]);
        }

        /// <summary>
        /// 路由表只读性：快照为独立副本，改返回字典不影响配置文件；
        /// 且序列化回环后 JSON 中确实存在 Serial.NodeRouting 段（手工可编辑）。
        /// </summary>
        [Fact]
        public void NodeRouting_SnapshotIsCopyAndPersistsInFile()
        {
            var svc = new ConfigService(_configPath);
            svc.SerialPortName = "COM3"; // 触发一次写盘，令 Serial 段落进 JSON

            var routing = svc.GetNodeRoutingSnapshot();
            ((Dictionary<byte, string>)routing)[0x2A] = "内存污染"; // 快照应与此无关

            var reloaded = new ConfigService(_configPath);
            Assert.Empty(reloaded.GetNodeRoutingSnapshot());
            Assert.Equal("COM3", reloaded.SerialPortName);
            Assert.Contains("\"Serial\"", File.ReadAllText(_configPath));
        }
    }
}
