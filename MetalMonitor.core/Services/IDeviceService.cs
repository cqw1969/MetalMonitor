namespace MetalMonitor.Core
{
    public interface IDeviceService
    {
        List<Device> GetDevices();
        void UpdateDevices();
        bool ShouldAlarm(double temperature);
        int MaxHistoryPoints { get; }
        double AlarmThreshold { get; }
        string DataSource { get; }
    }
}