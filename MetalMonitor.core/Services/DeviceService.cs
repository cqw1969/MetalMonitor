using System.Text.Json;

namespace MetalMonitor.Core
{
    public class DeviceService : IDeviceService
    {
        private readonly List<Device> _devices;
        private readonly IAlarmService _alarmService;
        private readonly IMqttService? _mqttService;
        private readonly IDatabaseService? _databaseService;
        private readonly Random _random = new();
        private readonly Dictionary<string, double> _lastSimValues = new(); // 模拟数据平滑用：上一值缓存
        private readonly ConfigService _configService;
        private bool _useMqtt = false;
        private bool _devicesLoaded = false;
        private List<MetricConfig> _metricConfigs = new();
        private string _selectedDataSource = "模拟数据";

        public int MaxHistoryPoints { get; } = 60;

        public string DataSource => _selectedDataSource;

        public string[] AvailableDataSources { get; } = new[] { "模拟数据", "MQTT", "串口" };

        public DeviceService(IAlarmService alarmService)
        {
            _alarmService = alarmService;
            _devices = new List<Device>();
            _configService = new ConfigService();
            _selectedDataSource = _configService.DataSource;
        }

        public DeviceService(IAlarmService alarmService, IMqttService mqttService) : this(alarmService)
        {
            _mqttService = mqttService;
            _mqttService.MessageReceived += OnMqttMessageReceived;
            _mqttService.Disconnected += OnMqttDisconnected;
        }

        public DeviceService(IAlarmService alarmService, IDatabaseService databaseService) : this(alarmService)
        {
            _databaseService = databaseService;
        }

        public DeviceService(IAlarmService alarmService, IMqttService mqttService, IDatabaseService databaseService) : this(alarmService, mqttService)
        {
            _databaseService = databaseService;
        }

        public async Task InitializeMqttAsync()
        {
            // 仅当保存的数据来源为 MQTT 时才在启动时预连接，避免抢占用户选择的数据源
            if (_selectedDataSource != "MQTT")
                return;

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
                    // 连接失败：回退到模拟数据
                    _useMqtt = false;
                    _selectedDataSource = "模拟数据";
                    _configService.DataSource = "模拟数据";
                }
            }
        }

        public async Task SetDataSourceAsync(string source)
        {
            _selectedDataSource = source;
            _configService.DataSource = source;
            switch (source)
            {
                case "MQTT":
                    if (_mqttService != null)
                    {
                        try
                        {
                            if (!_mqttService.IsConnected)
                            {
                                await _mqttService.ConnectAsync();
                                await _mqttService.SubscribeAsync("metalmonitor/devices/#");
                            }
                            _useMqtt = true;
                        }
                        catch
                        {
                            // 连接失败：回退到模拟数据，由 UI 层同步回下拉框
                            _useMqtt = false;
                            _selectedDataSource = "模拟数据";
                            _configService.DataSource = "模拟数据";
                        }
                    }
                    else
                    {
                        _useMqtt = false;
                        _selectedDataSource = "模拟数据";
                        _configService.DataSource = "模拟数据";
                    }
                    break;
                case "串口":
                    // 串口尚未实现，暂时回退到模拟数据生成
                    _useMqtt = false;
                    if (_mqttService != null && _mqttService.IsConnected)
                    {
                        try { await _mqttService.DisconnectAsync(); } catch { }
                    }
                    break;
                default: // 模拟数据
                    _useMqtt = false;
                    if (_mqttService != null && _mqttService.IsConnected)
                    {
                        try { await _mqttService.DisconnectAsync(); } catch { }
                    }
                    break;
            }
        }

        public async Task ReloadMetricConfigsAsync()
        {
            if (_databaseService != null)
            {
                _metricConfigs = await _databaseService.GetAllMetricConfigsAsync();
                var dict = new Dictionary<string, MetricConfig>();
                foreach (var c in _metricConfigs)
                    dict[c.Name] = c;
                _alarmService.UpdateThresholds(dict);
            }
        }

        public List<MetricConfig> GetMetricConfigs()
        {
            return _metricConfigs;
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

                bool isAlarming = _alarmService.ShouldAlarm("Temperature", device.Temperature);
                device.SetAlarmState(isAlarming);

                _alarmService.CheckAndNotify(device.Name, "Temperature", device.Temperature);
            }
        }

        private async Task UpdateDeviceDataAsync()
        {
            var batch = new List<DataRecord>();
            foreach (var device in _devices)
            {
                if (!device.IsActive) continue;
                foreach (var metric in _metricConfigs)
                {
                    if (!metric.IsActive) continue;
                    double value = GenerateSmoothValue(device.Id, metric);
                    if (metric.Name == "Temperature")
                    {
                        device.Temperature = value;
                        device.SetAlarmState(_alarmService.ShouldAlarm("Temperature", value));
                        _alarmService.CheckAndNotify(device.Name, "Temperature", value);
                    }
                    if (_databaseService != null)
                    {
                        batch.Add(new DataRecord
                        {
                            DeviceId = device.Id,
                            DeviceName = device.Name,
                            MetricName = metric.Name,
                            Value = value,
                            Unit = metric.Unit,
                            Timestamp = DateTime.Now
                        });
                    }
                }
            }
            if (_databaseService != null && batch.Count > 0)
            {
                // 批量插入，减少连接开销和锁冲突
                await _databaseService.AddDataRecordsBatchAsync(batch);
            }
        }

        // 平滑随机游走：围绕上一值小幅波动，避免曲线剧烈抖动
        private double GenerateSmoothValue(string deviceId, MetricConfig metric)
        {
            string key = $"{deviceId}:{metric.Name}";
            double span = metric.MaxValue - metric.MinValue;
            if (span <= 0) span = 1;

            if (!_lastSimValues.TryGetValue(key, out double last))
                last = metric.MinValue + span * 0.5;

            double next = last + (_random.NextDouble() - 0.5) * span * 0.1;
            next = Math.Clamp(next, metric.MinValue, metric.MaxValue);
            _lastSimValues[key] = next;
            return Math.Round(next, 1);
        }

        public List<Device> GetDevices()
        {
            return _devices.AsReadOnly().ToList();
        }

        private void OnMqttDisconnected(object? sender, EventArgs e)
        {
            _useMqtt = false;
        }

        public async Task UpdateDevicesAsync()
        {
            // 首次加载设备列表和指标配置
            if (!_devicesLoaded && _databaseService != null)
            {
                var dbDevices = await _databaseService.GetAllDevicesAsync();
                _devices.Clear();
                foreach (var d in dbDevices)
                {
                    _devices.Add(d);
                }
                _devicesLoaded = true;

                if (_metricConfigs.Count == 0)
                {
                    await ReloadMetricConfigsAsync();
                }
            }

            if (!_useMqtt || !_mqttService?.IsConnected == true)
            {
                await UpdateDeviceDataAsync();
            }
        }

        public bool ShouldAlarm(string metricName, double value)
        {
            return _alarmService.ShouldAlarm(metricName, value);
        }

        public double GetAlarmThreshold(string metricName)
        {
            return _alarmService.GetAlarmThreshold(metricName);
        }

        public async Task ReloadDevicesAsync()
        {
            if (_databaseService != null)
            {
                var dbDevices = await _databaseService.GetAllDevicesAsync();
                _devices.Clear();
                foreach (var d in dbDevices)
                {
                    _devices.Add(d);
                }
                _devicesLoaded = true;
            }
        }

        private class MqttDeviceData
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public double Temperature { get; set; }
        }
    }
}
