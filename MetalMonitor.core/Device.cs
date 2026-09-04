using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MetalMonitor.Core
{
    public class Device : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _name = string.Empty;
        private string _deviceType = string.Empty;
        private string _location = string.Empty;
        private string _description = string.Empty;
        private bool _isActive = true;
        private double _temperature;
        private bool _isAlarming;
        private DateTime _createdAt;

        // 各指标的当前值（指标名 -> 值），跨线程访问
        private readonly ConcurrentDictionary<string, double> _metricValues = new();

        // 各指标的报警状态（指标名 -> 是否报警）
        private readonly ConcurrentDictionary<string, bool> _metricAlarms = new();

        // 指标值更新事件：参数为指标名和最新值
        public event Action<string, double>? MetricValueChanged;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string DeviceType
        {
            get => _deviceType;
            set => SetProperty(ref _deviceType, value);
        }

        public string Location
        {
            get => _location;
            set => SetProperty(ref _location, value);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        public double Temperature
        {
            get => _temperature;
            set => SetProperty(ref _temperature, value);
        }

        public bool IsAlarming
        {
            get => _isAlarming;
            private set => SetProperty(ref _isAlarming, value);
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set => SetProperty(ref _createdAt, value);
        }

        // 设置报警状态（由服务层调用）
        public void SetAlarmState(bool isAlarming)
        {
            IsAlarming = isAlarming;
        }

        // 写入某指标的当前值（由服务层调用）
        public void SetMetricValue(string metricName, double value)
        {
            _metricValues[metricName] = value;
            MetricValueChanged?.Invoke(metricName, value);
            OnPropertyChanged(nameof(MetricValues));
        }

        // 读取某指标当前值；无数据返回 NaN
        public double GetMetricValue(string metricName)
        {
            return _metricValues.TryGetValue(metricName, out var v) ? v : double.NaN;
        }

        // 所有指标当前值快照（供 UI 绑定）
        public IReadOnlyDictionary<string, double> MetricValues => _metricValues;

        // 写入某指标的报警状态，并汇总设备整体报警状态
        public void SetMetricAlarmState(string metricName, bool isAlarming)
        {
            _metricAlarms[metricName] = isAlarming;
            IsAlarming = _metricAlarms.Values.Any(a => a);
        }

        public bool IsMetricAlarming(string metricName)
        {
            return _metricAlarms.TryGetValue(metricName, out var a) && a;
        }

        // 无参构造函数（用于数据库读取等场景）
        public Device()
        {
            Id = Guid.NewGuid().ToString();
            _createdAt = DateTime.Now;
        }

        // 构造一个方便测试的设备
        public Device(string name, double initialTemp = 60)
        {
            Id = Guid.NewGuid().ToString();
            Name = name;
            _temperature = initialTemp;
            IsAlarming = false;
            _createdAt = DateTime.Now;
        }

        // 用于设备管理的构造函数
        public Device(string name, string deviceType, string location, string description = "")
        {
            Id = Guid.NewGuid().ToString();
            Name = name;
            _deviceType = deviceType;
            _location = location;
            _description = description;
            _isActive = true;
            _temperature = 0;
            IsAlarming = false;
            _createdAt = DateTime.Now;
        }

        // 实现 INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}