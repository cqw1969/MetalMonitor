namespace MetalMonitor.Core
{
    public class SimulatedDataService : IDataService
    {
        private readonly List<Device> _devices;
        private readonly IAlarmService _alarmService;
        private readonly Random _random = new();

        //通过构造函数注入 IAlarmService，使得 SimulatedDataService 可以使用报警服务的逻辑
        public SimulatedDataService(IAlarmService alarmService)
        {
            _alarmService = alarmService;
            _devices = new List<Device>
            {
                new Device("1号冲压机", 65),
                new Device("2号CNC", 60),
                new Device("3号磨床", 70)
            };
        }

        // 更新设备数据（内部方法，负责生成新数据）
        private void UpdateDeviceData()
        {
            foreach (var device in _devices)
            {
                double newTemp = 55 + _random.NextDouble() * 35;
                device.Temperature = Math.Round(newTemp, 1);

                // 由报警服务统一判断并设置报警状态
                bool isAlarming = _alarmService.ShouldAlarm(device.Temperature);
                device.SetAlarmState(isAlarming);

                // 检查报警状态变化并通知
                _alarmService.CheckAndNotify(device.Name, device.Temperature);
            }
        }

        public List<Device> GetAllDevices()
        {
            // 更新数据后返回
            UpdateDeviceData();
            // 返回只读副本，防止外部修改内部状态
            return _devices.AsReadOnly().ToList();
        }
    }
}