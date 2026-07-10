namespace MetalMonitor.Core
{
    public interface IDeviceService
    {
        List<Device> GetDevices();
        Task UpdateDevicesAsync();
        bool ShouldAlarm(double temperature);
        int MaxHistoryPoints { get; }
        double AlarmThreshold { get; }
        string DataSource { get; }
    }
}