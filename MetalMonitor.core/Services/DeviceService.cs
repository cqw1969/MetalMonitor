namespace MetalMonitor.Core
{
    public class DeviceService : IDeviceService
    {
        private readonly List<Device> _devices;
        private readonly IAlarmService _alarmService;
        private readonly Random _random = new();

        public int MaxHistoryPoints { get; } = 60;

        public double AlarmThreshold => _alarmService.AlarmThreshold;

        public DeviceService(IAlarmService alarmService)
        {
            _alarmService = alarmService;
            _devices = new List<Device>
            {
                new Device("1号冲压机", 65),
                new Device("2号CNC", 60),
                new Device("3号磨床", 70)
            };
        }

        private void UpdateDeviceData()
        {
            foreach (var device in _devices)
            {
                double newTemp = 55 + _random.NextDouble() * 35;
                device.Temperature = Math.Round(newTemp, 1);

                bool isAlarming = _alarmService.ShouldAlarm(device.Temperature);
                device.SetAlarmState(isAlarming);

                _alarmService.CheckAndNotify(device.Name, device.Temperature);
            }
        }

        public List<Device> GetDevices()
        {
            return _devices.AsReadOnly().ToList();
        }

        public void UpdateDevices()
        {
            UpdateDeviceData();
        }

        public bool ShouldAlarm(double temperature)
        {
            return _alarmService.ShouldAlarm(temperature);
        }
    }
}