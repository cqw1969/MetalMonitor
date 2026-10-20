using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    /// <summary>
    /// SQLite 落盘层（持久化边界）。
    /// 设计模型：短连接——每次操作新开 SqliteConnection 用完即弃（连接池兜底），
    /// 不共享连接换来无并发状态的简单性；高频写入靠 AddDataRecordsBatchAsync
    /// 单事务批量摊薄提交开销；WAL 空间回收由 CheckpointAsync 周期执行。
    /// </summary>
    public class DatabaseService : IDatabaseService
    {
        /// <summary>SQLite 数据库文件（metalmonitor.db）的完整路径，支持运行时切换。</summary>
        private string _databasePath;
        /// <summary>本服务自建的配置访问入口，用于读取/持久化数据库目录设置。</summary>
        private readonly ConfigService _configService;

        /// <summary>获取当前 SQLite 数据库文件的完整路径。</summary>
        public string DatabasePath => _databasePath;

        /// <summary>
        /// 按 config.json 中配置的目录初始化数据库路径；
        /// 配置为空时回退到 %AppData%/MetalMonitor/metalmonitor.db 并确保目录存在。
        /// </summary>
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
        /// <summary>
        /// 以显式指定的数据库文件完整路径初始化，并确保其父目录存在。
        /// </summary>
        /// <param name="databasePath">metalmonitor.db 的完整文件路径。</param>
        public DatabaseService(string databasePath)
        {
            _configService = new ConfigService();
            _databasePath = databasePath;
            string? dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// 运行时切换数据库目录：更新内存路径、写回 config.json，并在新库上执行初始化建表。
        /// </summary>
        /// <param name="newPath">新的数据库所在目录（不含文件名）。</param>
        /// <returns>表示异步切换与初始化操作的任务。</returns>
        public async Task ChangeDatabasePathAsync(string newPath)
        {
            _databasePath = Path.Combine(newPath, "metalmonitor.db");
            _configService.DatabasePath = newPath;
            await InitializeAsync().ConfigureAwait(false);
        }

        // 周期回收 WAL 文件空间（与写事务错峰执行，异常由调用方兜底）
        /// <summary>
        /// 执行 WAL 检查点（TRUNCATE）：将 WAL 文件内容合并回主库并截断 WAL 文件以回收磁盘空间。
        /// </summary>
        /// <returns>表示异步检查点操作的任务。</returns>
        /// <remarks>使用独立短连接；异常不在本方法内处理，由周期调用方兜底吞掉。</remarks>
        public async Task CheckpointAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 初始化数据库：开启 WAL 模式、执行旧库结构迁移、创建 DataRecords/Devices/MetricConfigs
        /// 三张表及索引，并在设备表/指标配置表为空时写入默认种子数据。
        /// </summary>
        /// <returns>表示异步初始化操作的任务。</returns>
        /// <remarks>所有建表语句均为 IF NOT EXISTS，重复调用安全；使用单条短连接完成全部操作。</remarks>
        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            // 启用 WAL 模式，允许读写并发，减少锁冲突
            using (var walCmd = new SqliteCommand("PRAGMA journal_mode=WAL;", connection))
            {
                await walCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 迁移：将旧的 MetricType 列重命名为 MetricName（旧库兼容）
            await MigrateDataRecordsSchemaAsync(connection).ConfigureAwait(false);

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
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            // 如果Devices表为空，插入默认设备
            await InsertDefaultDevicesIfEmptyAsync(connection).ConfigureAwait(false);

            // 如果MetricConfigs表为空，插入默认指标配置
            await InsertDefaultMetricConfigsIfEmptyAsync(connection).ConfigureAwait(false);
        }

        /// <summary>
        /// DataRecords 表结构迁移：旧库存在 MetricType 列且缺少 MetricName 列时，将列重命名为 MetricName。
        /// </summary>
        /// <param name="connection">已打开的 SQLite 连接（与初始化流程共用）。</param>
        /// <returns>表示异步迁移操作的任务；无需迁移时立即完成。</returns>
        /// <remarks>通过 PRAGMA table_info 探测实际列结构，避免对新库或已迁移库重复执行。</remarks>
        private async Task MigrateDataRecordsSchemaAsync(SqliteConnection connection)
        {
            // 检查 DataRecords 表是否存在以及列结构
            bool hasMetricName = false;
            bool hasMetricType = false;
            using (var checkCmd = new SqliteCommand("PRAGMA table_info(DataRecords)", connection))
            using (var reader = await checkCmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
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
                await renameCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Devices 表为空时插入内置的三台默认设备（1号冲压机、2号CNC、3号磨床）。
        /// </summary>
        /// <param name="connection">已打开的 SQLite 连接（与初始化流程共用）。</param>
        /// <returns>表示异步种子写入操作的任务；表非空时立即完成。</returns>
        private async Task InsertDefaultDevicesIfEmptyAsync(SqliteConnection connection)
        {
            var countSql = "SELECT COUNT(*) FROM Devices";
            using var countCmd = new SqliteCommand(countSql, connection);
            var count = (long)(await countCmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
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
                await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// MetricConfigs 表为空时插入六项内置指标配置（温度、电压、产量、湿度、压力、转速）。
        /// </summary>
        /// <param name="connection">已打开的 SQLite 连接（与初始化流程共用）。</param>
        /// <returns>表示异步种子写入操作的任务；表非空时立即完成。</returns>
        private async Task InsertDefaultMetricConfigsIfEmptyAsync(SqliteConnection connection)
        {
            var countSql = "SELECT COUNT(*) FROM MetricConfigs";
            using var countCmd = new SqliteCommand(countSql, connection);
            var count = (long)(await countCmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
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
                await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 插入单条数据记录（独立短连接、自动提交事务）。
        /// </summary>
        /// <param name="deviceId">设备唯一标识。</param>
        /// <param name="deviceName">设备名称（随记录冗余存储，便于历史查询直接展示）。</param>
        /// <param name="metricName">指标名称。</param>
        /// <param name="value">指标值。</param>
        /// <param name="unit">指标单位；为 null 时以空字符串入库。</param>
        /// <returns>表示异步插入操作的任务。</returns>
        public async Task AddDataRecordAsync(string deviceId, string deviceName, string metricName, double value, string? unit = null)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // 单事务批量插入：全部 INSERT 在同一事务内一次提交——中途任一条失败整体回滚，
        // 且只付一次磁盘同步代价，是"攒够 N 条一次写"策略的底座
        /// <summary>
        /// 在单个事务内批量插入数据记录，一次提交。
        /// </summary>
        /// <param name="records">待写入的记录集合；为 null 或空集合时直接返回。</param>
        /// <returns>表示异步批量插入操作的任务。</returns>
        /// <remarks>
        /// 全部 INSERT 共用一个短连接与一个显式事务：任一条失败则整批回滚，
        /// 成功时只支付一次事务提交的磁盘同步代价，是批量缓冲落库策略的底座。
        /// </remarks>
        public async Task AddDataRecordsBatchAsync(List<DataRecord> records)
        {
            if (records == null || records.Count == 0)
                return;

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }

        /// <summary>
        /// 按可选条件查询数据记录，按时间戳倒序返回。
        /// </summary>
        /// <param name="deviceId">可选设备 Id 过滤；为 null 或空时不按设备过滤。</param>
        /// <param name="metricName">可选指标名过滤；为 null 或空时不按指标过滤。</param>
        /// <returns>命中的数据记录列表（最新在前）；无命中时返回空列表。</returns>
        /// <remarks>查询条件以参数化 SQL 拼接，避免字符串拼接注入与计划失效。</remarks>
        public async Task<List<DataRecord>> GetDataRecordsAsync(string? deviceId = null, string? metricName = null)
        {
            var records = new List<DataRecord>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
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

        /// <summary>
        /// 查询指定时间范围内（含端点）的数据记录，支持可选设备/指标过滤，按时间戳正序返回。
        /// </summary>
        /// <param name="startTime">起始时间（含）。</param>
        /// <param name="endTime">结束时间（含）。</param>
        /// <param name="deviceId">可选设备 Id 过滤；为 null 或空时不按设备过滤。</param>
        /// <param name="metricName">可选指标名过滤；为 null 或空时不按指标过滤。</param>
        /// <returns>命中的数据记录列表（最早在前）；无命中时返回空列表。</returns>
        public async Task<List<DataRecord>> GetDataRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, string? deviceId = null, string? metricName = null)
        {
            var records = new List<DataRecord>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
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

        /// <summary>
        /// 查询数据记录表中出现过的全部去重设备 Id。
        /// </summary>
        /// <returns>设备 Id 字符串列表；无记录时返回空列表。</returns>
        public async Task<List<string>> GetDeviceIdsAsync()
        {
            var deviceIds = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            var selectSql = "SELECT DISTINCT DeviceId FROM DataRecords";

            using var command = new SqliteCommand(selectSql, connection);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                deviceIds.Add(reader.GetString(0));
            }

            return deviceIds;
        }

        /// <summary>
        /// 查询数据记录表中出现过的全部去重指标名，可限定到某台设备。
        /// </summary>
        /// <param name="deviceId">可选设备 Id 过滤；为 null 或空时查询全部设备的指标。</param>
        /// <returns>指标名称列表；无记录时返回空列表。</returns>
        public async Task<List<string>> GetMetricNamesAsync(string? deviceId = null)
        {
            var metricNames = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                metricNames.Add(reader.GetString(0));
            }

            return metricNames;
        }

        // ===== 设备管理CRUD =====

        /// <summary>查询全部设备，按创建时间正序返回。</summary>
        /// <returns>设备实体列表；表为空时返回空列表。</returns>
        public async Task<List<Device>> GetAllDevicesAsync()
        {
            var devices = new List<Device>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            var selectSql = "SELECT Id, Name, DeviceType, Location, Description, IsActive, CreatedAt FROM Devices ORDER BY CreatedAt ASC";

            using var command = new SqliteCommand(selectSql, connection);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
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

        /// <summary>
        /// 新增一台设备；实体中的 Id、创建时间等字段按原值写入。
        /// </summary>
        /// <param name="device">待入库的设备实体；null 文本字段以空字符串入库。</param>
        /// <returns>表示异步插入操作的任务。</returns>
        public async Task AddDeviceAsync(Device device)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 按 Id 更新设备的名称、类型、位置、描述与启用状态（创建时间不变）。
        /// </summary>
        /// <param name="device">携带新字段值的设备实体，以其 Id 作为更新条件。</param>
        /// <returns>表示异步更新操作的任务；Id 不存在时影响行数为 0。</returns>
        public async Task UpdateDeviceAsync(Device device)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            var updateSql = @"UPDATE Devices SET Name = @Name, DeviceType = @DeviceType, Location = @Location,
                              Description = @Description, IsActive = @IsActive WHERE Id = @Id";

            using var command = new SqliteCommand(updateSql, connection);
            command.Parameters.AddWithValue("@Id", device.Id);
            command.Parameters.AddWithValue("@Name", device.Name);
            command.Parameters.AddWithValue("@DeviceType", device.DeviceType ?? "");
            command.Parameters.AddWithValue("@Location", device.Location ?? "");
            command.Parameters.AddWithValue("@Description", device.Description ?? "");
            command.Parameters.AddWithValue("@IsActive", device.IsActive ? 1 : 0);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 按 Id 删除设备（仅删 Devices 表行，不连带删除其历史数据记录）。
        /// </summary>
        /// <param name="deviceId">待删除设备的唯一标识。</param>
        /// <returns>表示异步删除操作的任务；Id 不存在时影响行数为 0。</returns>
        public async Task DeleteDeviceAsync(string deviceId)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            var deleteSql = "DELETE FROM Devices WHERE Id = @Id";
            using var command = new SqliteCommand(deleteSql, connection);
            command.Parameters.AddWithValue("@Id", deviceId);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // ===== 指标配置CRUD =====

        /// <summary>查询全部指标配置，按 Id 正序返回。</summary>
        /// <returns>指标配置实体列表；表为空时返回空列表。</returns>
        public async Task<List<MetricConfig>> GetAllMetricConfigsAsync()
        {
            var configs = new List<MetricConfig>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            var selectSql = @"SELECT Id, Name, DisplayName, Unit, AlarmThreshold, AlarmEnabled, AlarmDirection, MinValue, MaxValue, IsActive
                              FROM MetricConfigs ORDER BY Id ASC";

            using var command = new SqliteCommand(selectSql, connection);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
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

        /// <summary>
        /// 新增一条指标配置，并将数据库生成的自增 Id 回写到传入实体。
        /// </summary>
        /// <param name="config">待入库的指标配置；方法返回后其 <c>Id</c> 被填充为新行主键。</param>
        /// <returns>表示异步插入操作的任务。</returns>
        public async Task AddMetricConfigAsync(MetricConfig config)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            config.Id = Convert.ToInt32(result);
        }

        /// <summary>
        /// 按 Id 更新指标配置的全部字段（名称、显示名、单位、报警阈值/使能/方向、量程、启用状态）。
        /// </summary>
        /// <param name="config">携带新字段值的指标配置，以其 Id 作为更新条件。</param>
        /// <returns>表示异步更新操作的任务；Id 不存在时影响行数为 0。</returns>
        public async Task UpdateMetricConfigAsync(MetricConfig config)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

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

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 按主键 Id 删除一条指标配置。
        /// </summary>
        /// <param name="id">MetricConfigs 表自增主键。</param>
        /// <returns>表示异步删除操作的任务；Id 不存在时影响行数为 0。</returns>
        public async Task DeleteMetricConfigAsync(int id)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            var deleteSql = "DELETE FROM MetricConfigs WHERE Id = @Id";
            using var command = new SqliteCommand(deleteSql, connection);
            command.Parameters.AddWithValue("@Id", id);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
