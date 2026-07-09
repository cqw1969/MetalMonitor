using System.Text.Json;

namespace MetalMonitor.Core
{
    public class DeviceService : IDeviceService
    {
        private readonly List<Device> _devices;
        private readonly IAlarmService _alarmService;
        private readonly IMqttService? _mqttService;
        private readonly Random _random = new();
        private bool _useMqtt = false;

        public int MaxHistoryPoints { get; } = 60;

        public double AlarmThreshold => _alarmService.AlarmThreshold;

        public string DataSource => _useMqtt ? "MQTT" : "模拟数据";

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

        public DeviceService(IAlarmService alarmService, IMqttService mqttService) : this(alarmService)
        {
            _mqttService = mqttService;
            _mqttService.MessageReceived += OnMqttMessageReceived;
        }

        public async Task InitializeMqttAsync()
        {
            if (_mqttService != null && !_mqttService.IsConnected)
            {
                try
                {
                    await _mqttService.ConnectAsync();
                    await _mqttService.SubscribeAsync("metalmonitor/devices/#");
                    _useMqtt = true;
                }
                catch
                {
                    _useMqtt = false;
                }
            }
        }

        private void OnMqttMessageReceived(object? sender, MqttMessageReceivedEventArgs e)
        {
            try
            {
                var deviceData = JsonSerializer.Deserialize<MqttDeviceData>(e.Payload);
                if (deviceData != null)
                {
                    UpdateDeviceFromMqtt(deviceData);
                }
            }
            catch
            {
            }
        }

        private void UpdateDeviceFromMqtt(MqttDeviceData deviceData)
        {
            var device = _devices.FirstOrDefault(d => d.Name == deviceData.Name);
            if (device != null)
            {
                device.Temperature = deviceData.Temperature;
                
                bool isAlarming = _alarmService.ShouldAlarm(device.Temperature);
                device.SetAlarmState(isAlarming);
                
                _alarmService.CheckAndNotify(device.Name, device.Temperature);
            }
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
            if (!_useMqtt)
            {
                UpdateDeviceData();
            }
        }

        public bool ShouldAlarm(double temperature)
        {
            return _alarmService.ShouldAlarm(temperature);
        }

        private class MqttDeviceData
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public double Temperature { get; set; }
        }
    }
}