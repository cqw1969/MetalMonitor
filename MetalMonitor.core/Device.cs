using System.ComponentModel;
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