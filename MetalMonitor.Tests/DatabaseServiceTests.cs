using MetalMonitor.Core;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// DatabaseService（SQLite 数据访问层）单元测试
    ///
    /// 使用真实的 SQLite 文件（放在系统临时目录，测试结束删除），
    /// 验证建表、默认数据、批量插入、条件查询、CRUD、WAL checkpoint 的完整链路。
    /// </summary>
    public class DatabaseServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _dbPath;
        private readonly DatabaseService _db;

        public DatabaseServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetalMonitorDbTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _dbPath = Path.Combine(_tempDir, "test.db");
            _db = new DatabaseService(_dbPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private static DataRecord Record(string deviceId, string deviceName, string metric, double value, System.DateTime? ts = null)
            => new()
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                MetricName = metric,
                Value = value,
                Unit = "℃",
                Timestamp = ts ?? System.DateTime.Now
            };

        [Fact]
        public async Task InitializeAsync_CreatesDefaultDevicesAndMetrics()
        {
            await _db.InitializeAsync();

            var devices = await _db.GetAllDevicesAsync();
            Assert.Equal(3, devices.Count);

            var configs = await _db.GetAllMetricConfigsAsync();
            Assert.Equal(6, configs.Count);
            Assert.Contains(configs, c => c.Name == "Temperature");
            Assert.Contains(configs, c => c.Name == "Speed");
        }

        [Fact]
        public async Task InitializeAsync_CalledTwice_DefaultsNotDuplicated()
        {
            await _db.InitializeAsync();
            await _db.InitializeAsync();

            Assert.Equal(3, (await _db.GetAllDevicesAsync()).Count);
            Assert.Equal(6, (await _db.GetAllMetricConfigsAsync()).Count);
        }

        [Fact]
        public async Task AddDataRecordsBatchAsync_InsertsAllRecords()
        {
            await _db.InitializeAsync();
            var records = Enumerable.Range(1, 5)
                .Select(i => Record("d1", "1号冲压机", "Temperature", 60 + i))
                .ToList();

            await _db.AddDataRecordsBatchAsync(records);

            var all = await _db.GetDataRecordsByTimeRangeAsync(System.DateTime.Now.AddDays(-1), System.DateTime.Now.AddDays(1));
            Assert.Equal(5, all.Count);
            Assert.Contains(all, r => r.Value == 65.0);
        }

        [Fact]
        public async Task AddDataRecordsBatchAsync_EmptyList_NoOp()
        {
            await _db.InitializeAsync();

            await _db.AddDataRecordsBatchAsync(new System.Collections.Generic.List<DataRecord>());

            Assert.Empty(await _db.GetDataRecordsByTimeRangeAsync(System.DateTime.Now.AddDays(-1), System.DateTime.Now.AddDays(1)));
        }

        [Fact]
        public async Task GetDataRecordsByTimeRangeAsync_FiltersByDeviceAndMetric()
        {
            await _db.InitializeAsync();
            await _db.AddDataRecordsBatchAsync(new()
            {
                Record("d1", "1号冲压机", "Temperature", 70),
                Record("d1", "1号冲压机", "Pressure", 8),
                Record("d2", "2号CNC", "Temperature", 75)
            });

            var start = System.DateTime.Now.AddDays(-1);
            var end = System.DateTime.Now.AddDays(1);

            var d1Only = await _db.GetDataRecordsByTimeRangeAsync(start, end, deviceId: "d1");
            Assert.Equal(2, d1Only.Count);
            Assert.All(d1Only, r => Assert.Equal("d1", r.DeviceId));

            var pressureOnly = await _db.GetDataRecordsByTimeRangeAsync(start, end, metricName: "Pressure");
            Assert.Single(pressureOnly);
            Assert.Equal(8, pressureOnly[0].Value);
        }

        [Fact]
        public async Task GetDataRecordsByTimeRangeAsync_ReturnsOnlyRecordsInWindow()
        {
            await _db.InitializeAsync();
            var now = System.DateTime.Now;
            await _db.AddDataRecordsBatchAsync(new()
            {
                Record("d1", "1号冲压机", "Temperature", 10, now.AddMinutes(-30)),
                Record("d1", "1号冲压机", "Temperature", 20, now),
                Record("d1", "1号冲压机", "Temperature", 30, now.AddMinutes(30))
            });

            var inWindow = await _db.GetDataRecordsByTimeRangeAsync(now.AddMinutes(-15), now.AddMinutes(15));

            Assert.Single(inWindow);
            Assert.Equal(20, inWindow[0].Value);
        }

        [Fact]
        public async Task DeviceCrud_AddUpdateDelete_Lifecycle()
        {
            await _db.InitializeAsync();
            var device = new Device("测试机", "CNC", "C车间");

            await _db.AddDeviceAsync(device);
            var devices = await _db.GetAllDevicesAsync();
            Assert.Contains(devices, d => d.Name == "测试机");

            device.Name = "测试机-改名";
            await _db.UpdateDeviceAsync(device);
            devices = await _db.GetAllDevicesAsync();
            Assert.Contains(devices, d => d.Name == "测试机-改名");

            await _db.DeleteDeviceAsync(device.Id);
            devices = await _db.GetAllDevicesAsync();
            Assert.DoesNotContain(devices, d => d.Id == device.Id);
        }

        [Fact]
        public async Task MetricConfigCrud_AddUpdateDelete_Lifecycle()
        {
            await _db.InitializeAsync();
            var config = new MetricConfig
            {
                Name = "TestMetric",
                DisplayName = "测试指标",
                Unit = "kPa",
                AlarmThreshold = 42.0,
                AlarmEnabled = true,
                AlarmDirection = AlarmDirection.Below,
                MinValue = 0,
                MaxValue = 100,
                IsActive = true
            };

            await _db.AddMetricConfigAsync(config);
            Assert.True(config.Id > 0);

            config.AlarmThreshold = 99.0;
            await _db.UpdateMetricConfigAsync(config);
            var reloaded = (await _db.GetAllMetricConfigsAsync()).First(c => c.Id == config.Id);
            Assert.Equal(99.0, reloaded.AlarmThreshold);
            Assert.Equal(AlarmDirection.Below, reloaded.AlarmDirection);

            await _db.DeleteMetricConfigAsync(config.Id);
            Assert.DoesNotContain(await _db.GetAllMetricConfigsAsync(), c => c.Id == config.Id);
        }

        [Fact]
        public async Task CheckpointAsync_AfterInserts_DoesNotThrowAndDataPersists()
        {
            await _db.InitializeAsync();
            await _db.AddDataRecordsBatchAsync(
                Enumerable.Range(1, 10).Select(i => Record("d1", "1号冲压机", "Temperature", 60 + i)).ToList());

            await _db.CheckpointAsync();

            Assert.Equal(10, (await _db.GetDataRecordsByTimeRangeAsync(System.DateTime.Now.AddDays(-1), System.DateTime.Now.AddDays(1))).Count);
        }
    }
}
