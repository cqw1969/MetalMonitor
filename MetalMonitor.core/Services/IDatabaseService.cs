using System.Collections.Generic;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    public interface IDatabaseService
    {
        string DatabasePath { get; }
        Task InitializeAsync();
        Task ChangeDatabasePathAsync(string newPath);
        Task CheckpointAsync();

        // 数据记录
        Task AddDataRecordAsync(string deviceId, string deviceName, string metricName, double value, string? unit = null);
        Task AddDataRecordsBatchAsync(List<DataRecord> records);
        Task<List<DataRecord>> GetDataRecordsAsync(string? deviceId = null, string? metricName = null);
        Task<List<DataRecord>> GetDataRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, string? deviceId = null, string? metricName = null);
        Task<List<string>> GetDeviceIdsAsync();
        Task<List<string>> GetMetricNamesAsync(string? deviceId = null);

        // 设备管理CRUD
        Task<List<Device>> GetAllDevicesAsync();
        Task AddDeviceAsync(Device device);
        Task UpdateDeviceAsync(Device device);
        Task DeleteDeviceAsync(string deviceId);

        // 指标配置CRUD
        Task<List<MetricConfig>> GetAllMetricConfigsAsync();
        Task AddMetricConfigAsync(MetricConfig config);
        Task UpdateMetricConfigAsync(MetricConfig config);
        Task DeleteMetricConfigAsync(int id);
    }
}
