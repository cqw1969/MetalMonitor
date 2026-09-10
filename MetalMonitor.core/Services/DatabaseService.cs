using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    public class DatabaseService : IDatabaseService
    {
        private string _databasePath;
        private readonly ConfigService _configService;

        public string DatabasePath => _databasePath;

        public DatabaseService()
        {
            _configService = new ConfigService();

            if (!string.IsNullOrEmpty(_configService.DatabasePath))
            {
                _databasePath = Path.Combine(_configService.DatabasePath, "metalmonitor.db");
            }
            else
            {
                var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetalMonitor");
                Directory.CreateDirectory(appDataPath);
                _databasePath = Path.Combine(appDataPath, "metalmonitor.db");
            }
        }

        // 显式指定数据库文件完整路径（便于单元测试隔离与自定义部署）
        public DatabaseService(string databasePath)
        {
            _configService = new ConfigService();
            _databasePath = databasePath;
            string? dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        public async Task ChangeDatabasePathAsync(string newPath)
        {
            _databasePath = Path.Combine(newPath, "metalmonitor.db");
            _configService.DatabasePath = newPath;
            await InitializeAsync();
        }

        // 周期回收 WAL 文件空间（与写事务错峰执行，异常由调用方兜底）
        public async Task CheckpointAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            using var command = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            // 启用 WAL 模式，允许读写并发，减少锁冲突
            using (var walCmd = new SqliteCommand("PRAGMA journal_mode=WAL;", connection))
            {
                await walCmd.ExecuteNonQueryAsync();
            }

            // 迁移：将旧的 MetricType 列重命名为 MetricName（旧库兼容）
            await MigrateDataRecordsSchemaAsync(connection);

            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS DataRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceId TEXT NOT NULL,
                    DeviceName TEXT NOT NULL,
                    MetricName TEXT NOT NULL,
                    Value REAL NOT NULL,
                    Unit TEXT,
                    Timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_DataRecords_DeviceId ON DataRecords(DeviceId);
                CREATE INDEX IF NOT EXISTS IX_DataRecords_MetricName ON DataRecords(MetricName);
                CREATE INDEX IF NOT EXISTS IX_DataRecords_Timestamp ON DataRecords(Timestamp);

                CREATE TABLE IF NOT EXISTS Devices (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    DeviceType TEXT NOT NULL DEFAULT '',
                    Location TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS MetricConfigs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    DisplayName TEXT,
                    Unit TEXT,
                    AlarmThreshold REAL,
                    AlarmEnabled INTEGER,
                    AlarmDirection INTEGER,
                    MinValue REAL,
                    MaxValue REAL,
                    IsActive INTEGER
                );
            ";

            using var command = new SqliteCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync();

            // 如果Devices表为空，插入默认设备
            await InsertDefaultDevicesIfEmptyAsync(connection);

            // 如果MetricConfigs表为空，插入默认指标配置
            await InsertDefaultMetricConfigsIfEmptyAsync(connection);
        }

        private async Task MigrateDataRecordsSchemaAsync(SqliteConnection connection)
        {
            // 检查 DataRecords 表是否存在以及列结构
            bool hasMetricName = false;
            bool hasMetricType = false;
            using (var checkCmd = new SqliteCommand("PRAGMA table_info(DataRecords)", connection))
            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var colName = reader.GetString(1);
                    if (colName == "MetricName") hasMetricName = true;
                    if (colName == "MetricType") hasMetricType = true;
                }
            }

            // 旧库存在 MetricType 列但缺少 MetricName 列，重命名迁移
            if (hasMetricType && !hasMetricName)
            {
                using var renameCmd = new SqliteCommand(
                    "ALTER TABLE DataRecords RENAME COLUMN MetricType TO MetricName", connection);
                await renameCmd.ExecuteNonQueryAsync();
            }
        }

        private async Task InsertDefaultDevicesIfEmptyAsync(SqliteConnection connection)
        {
            var countSql = "SELECT COUNT(*) FROM Devices";
            using var countCmd = new SqliteCommand(countSql, connection);
            var count = (long)await countCmd.ExecuteScalarAsync();
            if (count > 0)
                return;

            var defaultDevices = new[]
            {
                new { Id = Guid.NewGuid().ToString(), Name = "1号冲压机", Type = "冲压机", Location = "A车间", Desc = "" },
                new { Id = Guid.NewGuid().ToString(), Name = "2号CNC", Type = "CNC", Location = "A车间", Desc = "" },
                new { Id = Guid.NewGuid().ToString(), Name = "3号磨床", Type = "磨床", Location = "B车间", Desc = "" }
            };

            var insertDeviceSql = @"INSERT INTO Devices (Id, Name, DeviceType, Location, Description, IsActive, CreatedAt)
                                     VALUES (@Id, @Name, @DeviceType, @Location, @Description, @IsActive, @CreatedAt)";
            foreach (var d in defaultDevices)
            {
                using var insertCmd = new SqliteCommand(insertDeviceSql, connection);
                insertCmd.Parameters.AddWithValue("@Id", d.Id);
                insertCmd.Parameters.AddWithValue("@Name", d.Name);
                insertCmd.Parameters.AddWithValue("@DeviceType", d.Type);
                insertCmd.Parameters.AddWithValue("@Location", d.Location);
                insertCmd.Parameters.AddWithValue("@Description", d.Desc);
                insertCmd.Parameters.AddWithValue("@IsActive", 1);
                insertCmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        private async Task InsertDefaultMetricConfigsIfEmptyAsync(SqliteConnection connection)
        {
            var countSql = "SELECT COUNT(*) FROM MetricConfigs";
            using var countCmd = new SqliteCommand(countSql, connection);
            var count = (long)await countCmd.ExecuteScalarAsync();
            if (count > 0)
                return;

            // Name, DisplayName, Unit, AlarmThreshold, AlarmEnabled, AlarmDirection, MinValue, MaxValue, IsActive
            var defaultMetrics = new[]
            {
                new { Name = "Temperature", DisplayName = "温度", Unit = "℃", Threshold = 85.0, Enabled = true, Direction = AlarmDirection.Above, Min = 0.0, Max = 150.0, Active = true },
                new { Name = "Voltage", DisplayName = "电压", Unit = "V", Threshold = 380.0, Enabled = true, Direction = AlarmDirection.Above, Min = 0.0, Max = 500.0, Active = true },
                new { Name = "ProductionCount", DisplayName = "产量", Unit = "件", Threshold = 9999.0, Enabled = true, Direction = AlarmDirection.Above, Min = 0.0, Max = 99999.0, Active = true },
                new { Name = "Humidity", DisplayName = "湿度", Unit = "%", Threshold = 90.0, Enabled = true, Direction = AlarmDirection.Above, Min = 0.0, Max = 100.0, Active = true },
                new { Name = "Pressure", DisplayName = "压力", Unit = "MPa", Threshold = 10.0, Enabled = true, Direction = AlarmDirection.Above, Min = 0.0, Max = 20.0, Active = true },
                new { Name = "Speed", DisplayName = "转速", Unit = "rpm", Threshold = 3000.0, Enabled = true, Direction = AlarmDirection.Above, Min = 0.0, Max = 5000.0, Active = true }
            };

            var insertSql = @"INSERT INTO MetricConfigs (Name, DisplayName, Unit, AlarmThreshold, AlarmEnabled, AlarmDirection, MinValue, MaxValue, IsActive)
                              VALUES (@Name, @DisplayName, @Unit, @AlarmThreshold, @AlarmEnabled, @AlarmDirection, @MinValue, @MaxValue, @IsActive)";
            foreach (var m in defaultMetrics)
            {
                using var insertCmd = new SqliteCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("@Name", m.Name);
                insertCmd.Parameters.AddWithValue("@DisplayName", m.DisplayName);
                insertCmd.Parameters.AddWithValue("@Unit", m.Unit);
                insertCmd.Parameters.AddWithValue("@AlarmThreshold", m.Threshold);
                insertCmd.Parameters.AddWithValue("@AlarmEnabled", m.Enabled ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@AlarmDirection", (int)m.Direction);
                insertCmd.Parameters.AddWithValue("@MinValue", m.Min);
                insertCmd.Parameters.AddWithValue("@MaxValue", m.Max);
                insertCmd.Parameters.AddWithValue("@IsActive", m.Active ? 1 : 0);
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        public async Task AddDataRecordAsync(string deviceId, string deviceName, string metricName, double value, string? unit = null)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var insertSql = @"
                INSERT INTO DataRecords (DeviceId, DeviceName, MetricName, Value, Unit, Timestamp)
                VALUES (@DeviceId, @DeviceName, @MetricName, @Value, @Unit, @Timestamp)
            ";

            using var command = new SqliteCommand(insertSql, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId);
            command.Parameters.AddWithValue("@DeviceName", deviceName);
            command.Parameters.AddWithValue("@MetricName", metricName);
            command.Parameters.AddWithValue("@Value", value);
            command.Parameters.AddWithValue("@Unit", unit ?? string.Empty);
            command.Parameters.AddWithValue("@Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            await command.ExecuteNonQueryAsync();
        }

        public async Task AddDataRecordsBatchAsync(List<DataRecord> records)
        {
            if (records == null || records.Count == 0)
                return;

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            var insertSql = @"
                INSERT INTO DataRecords (DeviceId, DeviceName, MetricName, Value, Unit, Timestamp)
                VALUES (@DeviceId, @DeviceName, @MetricName, @Value, @Unit, @Timestamp)
            ";

            foreach (var record in records)
            {
                using var command = new SqliteCommand(insertSql, connection, transaction);
                command.Parameters.AddWithValue("@DeviceId", record.DeviceId);
                command.Parameters.AddWithValue("@DeviceName", record.DeviceName);
                command.Parameters.AddWithValue("@MetricName", record.MetricName);
                command.Parameters.AddWithValue("@Value", record.Value);
                command.Parameters.AddWithValue("@Unit", record.Unit ?? string.Empty);
                command.Parameters.AddWithValue("@Timestamp", record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));

                await command.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        public async Task<List<DataRecord>> GetDataRecordsAsync(string? deviceId = null, string? metricName = null)
        {
            var records = new List<DataRecord>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var selectSql = "SELECT Id, DeviceId, DeviceName, MetricName, Value, Unit, Timestamp FROM DataRecords WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(deviceId))
            {
                selectSql += " AND DeviceId = @DeviceId";
                parameters.Add(new SqliteParameter("@DeviceId", deviceId));
            }

            if (!string.IsNullOrEmpty(metricName))
            {
                selectSql += " AND MetricName = @MetricName";
                parameters.Add(new SqliteParameter("@MetricName", metricName));
            }

            selectSql += " ORDER BY Timestamp DESC";

            using var command = new SqliteCommand(selectSql, connection);
            command.Parameters.AddRange(parameters.ToArray());

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new DataRecord
                {
                    Id = reader.GetInt32(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.GetString(2),
                    MetricName = reader.GetString(3),
                    Value = reader.GetDouble(4),
                    Unit = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Timestamp = DateTime.Parse(reader.GetString(6))
                });
            }

            return records;
        }

        public async Task<List<DataRecord>> GetDataRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, string? deviceId = null, string? metricName = null)
        {
            var records = new List<DataRecord>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var selectSql = @"
                SELECT Id, DeviceId, DeviceName, MetricName, Value, Unit, Timestamp
                FROM DataRecords 
                WHERE Timestamp >= @StartTime AND Timestamp <= @EndTime
            ";
            var parameters = new List<SqliteParameter>
            {
                new SqliteParameter("@StartTime", startTime.ToString("yyyy-MM-dd HH:mm:ss")),
                new SqliteParameter("@EndTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"))
            };

            if (!string.IsNullOrEmpty(deviceId))
            {
                selectSql += " AND DeviceId = @DeviceId";
                parameters.Add(new SqliteParameter("@DeviceId", deviceId));
            }

            if (!string.IsNullOrEmpty(metricName))
            {
                selectSql += " AND MetricName = @MetricName";
                parameters.Add(new SqliteParameter("@MetricName", metricName));
            }

            selectSql += " ORDER BY Timestamp ASC";

            using var command = new SqliteCommand(selectSql, connection);
            command.Parameters.AddRange(parameters.ToArray());

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new DataRecord
                {
                    Id = reader.GetInt32(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.GetString(2),
                    MetricName = reader.GetString(3),
                    Value = reader.GetDouble(4),
                    Unit = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Timestamp = DateTime.Parse(reader.GetString(6))
                });
            }

            return records;
        }

        public async Task<List<string>> GetDeviceIdsAsync()
        {
            var deviceIds = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var selectSql = "SELECT DISTINCT DeviceId FROM DataRecords";

            using var command = new SqliteCommand(selectSql, connection);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                deviceIds.Add(reader.GetString(0));
            }

            return deviceIds;
        }

        public async Task<List<string>> GetMetricNamesAsync(string? deviceId = null)
        {
            var metricNames = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            string selectSql;
            if (!string.IsNullOrEmpty(deviceId))
            {
                selectSql = "SELECT DISTINCT MetricName FROM DataRecords WHERE DeviceId = @DeviceId";
            }
            else
            {
                selectSql = "SELECT DISTINCT MetricName FROM DataRecords";
            }

            using var command = new SqliteCommand(selectSql, connection);
            if (!string.IsNullOrEmpty(deviceId))
            {
                command.Parameters.AddWithValue("@DeviceId", deviceId);
            }

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                metricNames.Add(reader.GetString(0));
            }

            return metricNames;
        }

        // ===== 设备管理CRUD =====

        public async Task<List<Device>> GetAllDevicesAsync()
        {
            var devices = new List<Device>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var selectSql = "SELECT Id, Name, DeviceType, Location, Description, IsActive, CreatedAt FROM Devices ORDER BY CreatedAt ASC";

            using var command = new SqliteCommand(selectSql, connection);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                devices.Add(new Device
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    DeviceType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Location = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Description = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    IsActive = reader.GetInt32(5) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(6))
                });
            }

            return devices;
        }

        public async Task AddDeviceAsync(Device device)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var insertSql = @"INSERT INTO Devices (Id, Name, DeviceType, Location, Description, IsActive, CreatedAt)
                              VALUES (@Id, @Name, @DeviceType, @Location, @Description, @IsActive, @CreatedAt)";

            using var command = new SqliteCommand(insertSql, connection);
            command.Parameters.AddWithValue("@Id", device.Id);
            command.Parameters.AddWithValue("@Name", device.Name);
            command.Parameters.AddWithValue("@DeviceType", device.DeviceType ?? "");
            command.Parameters.AddWithValue("@Location", device.Location ?? "");
            command.Parameters.AddWithValue("@Description", device.Description ?? "");
            command.Parameters.AddWithValue("@IsActive", device.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("@CreatedAt", device.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

            await command.ExecuteNonQueryAsync();
        }

        public async Task UpdateDeviceAsync(Device device)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var updateSql = @"UPDATE Devices SET Name = @Name, DeviceType = @DeviceType, Location = @Location,
                              Description = @Description, IsActive = @IsActive WHERE Id = @Id";

            using var command = new SqliteCommand(updateSql, connection);
            command.Parameters.AddWithValue("@Id", device.Id);
            command.Parameters.AddWithValue("@Name", device.Name);
            command.Parameters.AddWithValue("@DeviceType", device.DeviceType ?? "");
            command.Parameters.AddWithValue("@Location", device.Location ?? "");
            command.Parameters.AddWithValue("@Description", device.Description ?? "");
            command.Parameters.AddWithValue("@IsActive", device.IsActive ? 1 : 0);

            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteDeviceAsync(string deviceId)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var deleteSql = "DELETE FROM Devices WHERE Id = @Id";
            using var command = new SqliteCommand(deleteSql, connection);
            command.Parameters.AddWithValue("@Id", deviceId);

            await command.ExecuteNonQueryAsync();
        }

        // ===== 指标配置CRUD =====

        public async Task<List<MetricConfig>> GetAllMetricConfigsAsync()
        {
            var configs = new List<MetricConfig>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var selectSql = @"SELECT Id, Name, DisplayName, Unit, AlarmThreshold, AlarmEnabled, AlarmDirection, MinValue, MaxValue, IsActive
                              FROM MetricConfigs ORDER BY Id ASC";

            using var command = new SqliteCommand(selectSql, connection);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                configs.Add(new MetricConfig
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DisplayName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Unit = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    AlarmThreshold = reader.GetDouble(4),
                    AlarmEnabled = reader.GetInt32(5) == 1,
                    AlarmDirection = (AlarmDirection)reader.GetInt32(6),
                    MinValue = reader.GetDouble(7),
                    MaxValue = reader.GetDouble(8),
                    IsActive = reader.GetInt32(9) == 1
                });
            }

            return configs;
        }

        public async Task AddMetricConfigAsync(MetricConfig config)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var insertSql = @"INSERT INTO MetricConfigs (Name, DisplayName, Unit, AlarmThreshold, AlarmEnabled, AlarmDirection, MinValue, MaxValue, IsActive)
                              VALUES (@Name, @DisplayName, @Unit, @AlarmThreshold, @AlarmEnabled, @AlarmDirection, @MinValue, @MaxValue, @IsActive);
                              SELECT last_insert_rowid();";

            using var command = new SqliteCommand(insertSql, connection);
            command.Parameters.AddWithValue("@Name", config.Name ?? "");
            command.Parameters.AddWithValue("@DisplayName", config.DisplayName ?? "");
            command.Parameters.AddWithValue("@Unit", config.Unit ?? "");
            command.Parameters.AddWithValue("@AlarmThreshold", config.AlarmThreshold);
            command.Parameters.AddWithValue("@AlarmEnabled", config.AlarmEnabled ? 1 : 0);
            command.Parameters.AddWithValue("@AlarmDirection", (int)config.AlarmDirection);
            command.Parameters.AddWithValue("@MinValue", config.MinValue);
            command.Parameters.AddWithValue("@MaxValue", config.MaxValue);
            command.Parameters.AddWithValue("@IsActive", config.IsActive ? 1 : 0);

            var result = await command.ExecuteScalarAsync();
            config.Id = Convert.ToInt32(result);
        }

        public async Task UpdateMetricConfigAsync(MetricConfig config)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var updateSql = @"UPDATE MetricConfigs SET Name = @Name, DisplayName = @DisplayName, Unit = @Unit,
                              AlarmThreshold = @AlarmThreshold, AlarmEnabled = @AlarmEnabled, AlarmDirection = @AlarmDirection,
                              MinValue = @MinValue, MaxValue = @MaxValue, IsActive = @IsActive
                              WHERE Id = @Id";

            using var command = new SqliteCommand(updateSql, connection);
            command.Parameters.AddWithValue("@Id", config.Id);
            command.Parameters.AddWithValue("@Name", config.Name ?? "");
            command.Parameters.AddWithValue("@DisplayName", config.DisplayName ?? "");
            command.Parameters.AddWithValue("@Unit", config.Unit ?? "");
            command.Parameters.AddWithValue("@AlarmThreshold", config.AlarmThreshold);
            command.Parameters.AddWithValue("@AlarmEnabled", config.AlarmEnabled ? 1 : 0);
            command.Parameters.AddWithValue("@AlarmDirection", (int)config.AlarmDirection);
            command.Parameters.AddWithValue("@MinValue", config.MinValue);
            command.Parameters.AddWithValue("@MaxValue", config.MaxValue);
            command.Parameters.AddWithValue("@IsActive", config.IsActive ? 1 : 0);

            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteMetricConfigAsync(int id)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var deleteSql = "DELETE FROM MetricConfigs WHERE Id = @Id";
            using var command = new SqliteCommand(deleteSql, connection);
            command.Parameters.AddWithValue("@Id", id);

            await command.ExecuteNonQueryAsync();
        }
    }
}
