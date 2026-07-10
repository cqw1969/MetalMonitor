using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    public class DatabaseService : IDatabaseService
    {
        private readonly string _databasePath;

        public DatabaseService()
        {
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetalMonitor");
            Directory.CreateDirectory(appDataPath);
            _databasePath = Path.Combine(appDataPath, "metalmonitor.db");
        }

        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS TemperatureRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceId TEXT NOT NULL,
                    DeviceName TEXT NOT NULL,
                    Temperature REAL NOT NULL,
                    Timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_TemperatureRecords_DeviceId ON TemperatureRecords(DeviceId);
                CREATE INDEX IF NOT EXISTS IX_TemperatureRecords_Timestamp ON TemperatureRecords(Timestamp);
            ";

            using var command = new SqliteCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task AddTemperatureRecordAsync(string deviceId, string deviceName, double temperature)
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var insertSql = @"
                INSERT INTO TemperatureRecords (DeviceId, DeviceName, Temperature, Timestamp)
                VALUES (@DeviceId, @DeviceName, @Temperature, @Timestamp)
            ";

            using var command = new SqliteCommand(insertSql, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId);
            command.Parameters.AddWithValue("@DeviceName", deviceName);
            command.Parameters.AddWithValue("@Temperature", temperature);
            command.Parameters.AddWithValue("@Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<TemperatureRecord>> GetTemperatureRecordsAsync(string deviceId = null)
        {
            var records = new List<TemperatureRecord>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            string selectSql;
            if (!string.IsNullOrEmpty(deviceId))
            {
                selectSql = "SELECT * FROM TemperatureRecords WHERE DeviceId = @DeviceId ORDER BY Timestamp DESC";
            }
            else
            {
                selectSql = "SELECT * FROM TemperatureRecords ORDER BY Timestamp DESC";
            }

            using var command = new SqliteCommand(selectSql, connection);
            if (!string.IsNullOrEmpty(deviceId))
            {
                command.Parameters.AddWithValue("@DeviceId", deviceId);
            }

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new TemperatureRecord
                {
                    Id = reader.GetInt32(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.GetString(2),
                    Temperature = reader.GetDouble(3),
                    Timestamp = DateTime.Parse(reader.GetString(4))
                });
            }

            return records;
        }

        public async Task<List<TemperatureRecord>> GetTemperatureRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime)
        {
            var records = new List<TemperatureRecord>();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var selectSql = @"
                SELECT * FROM TemperatureRecords 
                WHERE Timestamp >= @StartTime AND Timestamp <= @EndTime 
                ORDER BY Timestamp ASC
            ";

            using var command = new SqliteCommand(selectSql, connection);
            command.Parameters.AddWithValue("@StartTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@EndTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new TemperatureRecord
                {
                    Id = reader.GetInt32(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.GetString(2),
                    Temperature = reader.GetDouble(3),
                    Timestamp = DateTime.Parse(reader.GetString(4))
                });
            }

            return records;
        }
    }
}