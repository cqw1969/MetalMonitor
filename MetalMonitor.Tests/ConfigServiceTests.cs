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
    }
}
