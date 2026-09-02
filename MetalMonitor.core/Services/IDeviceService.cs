namespace MetalMonitor.Core
{
    public interface IDeviceService
    {
        List<Device> GetDevices();
        Task UpdateDevicesAsync();
        bool ShouldAlarm(string metricName, double value);
        double GetAlarmThreshold(string metricName);
        int MaxHistoryPoints { get; }
        string DataSource { get; }
        string[] AvailableDataSources { get; }
        Task SetDataSourceAsync(string source);
        Task ReloadDevicesAsync();
        Task ReloadMetricConfigsAsync();
        List<MetricConfig> GetMetricConfigs();
    }
}
