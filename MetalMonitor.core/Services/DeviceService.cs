using System.Text.Json;

namespace MetalMonitor.Core
{
    public class DeviceService : IDeviceService
    {
        private readonly List<Device> _devices;
        private readonly object _devicesLock = new();
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
                device.SetMetricValue("Temperature", deviceData.Temperature);
                device.Temperature = deviceData.Temperature;

                bool isAlarming = _alarmService.ShouldAlarm("Temperature", deviceData.Temperature);
                device.SetMetricAlarmState("Temperature", isAlarming);

                _alarmService.CheckAndNotify(device.Name, "Temperature", deviceData.Temperature);
            }
        }

        private async Task UpdateDeviceDataAsync()
        {
            var batch = new List<DataRecord>();
            // 快照配置，避免与 UI 线程重载配置竞争
            var metrics = _metricConfigs;
            // 快照设备列表，避免与 ReloadDevices 并发修改导致枚举异常
            List<Device> snapshot;
            lock (_devicesLock)
            {
                snapshot = _devices.AsReadOnly().ToList();
            }
            foreach (var device in snapshot)
            {
                if (!device.IsActive) continue;
                foreach (var metric in metrics)
                {
                    if (!metric.IsActive)
                    {
                        // 停用指标：清除报警状态
                        device.SetMetricAlarmState(metric.Name, false);
                        continue;
                    }
                    double value = GenerateSmoothValue(device.Id, metric);

                    // 每指标独立报警检测
                    bool alarming = _alarmService.ShouldAlarm(metric.Name, value);

                    // 先更新报警状态，再写入值触发 UI 事件，
                    // 保证 UI 线程处理值更新回调时读到的报警状态与本拍值一致（避免跨线程竞态导致报警滞后）
                    device.SetMetricAlarmState(metric.Name, alarming);
                    device.SetMetricValue(metric.Name, value);
                    _alarmService.CheckAndNotify(device.Name, metric.Name, value);

                    // 兼容旧的 Temperature 主属性
                    if (metric.Name == "Temperature")
                    {
                        device.Temperature = value;
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
            lock (_devicesLock)
            {
                return _devices.AsReadOnly().ToList();
            }
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
                await SyncDevicesFromDatabaseAsync();

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

        // 增量同步设备列表：保留 Id 相同的旧对象（卡片 ViewModel 仍持有引用可继续收事件），
        // 移除已删除的，追加新增的。避免 Clear+重建导致引用失效和并发枚举异常。
        private async Task SyncDevicesFromDatabaseAsync()
        {
            if (_databaseService == null) return;
            var dbDevices = await _databaseService.GetAllDevicesAsync();

            lock (_devicesLock)
            {
                var existingIds = _devices.Select(d => d.Id).ToHashSet();
                var dbIds = dbDevices.Select(d => d.Id).ToHashSet();

                // 移除被删除的
                for (int i = _devices.Count - 1; i >= 0; i--)
                {
                    if (!dbIds.Contains(_devices[i].Id))
                    {
                        _devices.RemoveAt(i);
                    }
                }

                // 追加新增的（保留 Id 相同的旧对象，仅更新属性以保持引用不变）
                var existingByDeviceId = _devices.ToDictionary(d => d.Id);
                foreach (var dbDevice in dbDevices)
                {
                    if (existingByDeviceId.TryGetValue(dbDevice.Id, out var existing))
                    {
                        // 复用旧对象，仅刷新可能被编辑过的字段
                        existing.Name = dbDevice.Name;
                        existing.DeviceType = dbDevice.DeviceType;
                        existing.Location = dbDevice.Location;
                        existing.Description = dbDevice.Description;
                        existing.IsActive = dbDevice.IsActive;
                    }
                    else
                    {
                        _devices.Add(dbDevice);
                    }
                }

                _devicesLoaded = true;
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
            await SyncDevicesFromDatabaseAsync();
        }

        private class MqttDeviceData
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public double Temperature { get; set; }
        }
    }
}
