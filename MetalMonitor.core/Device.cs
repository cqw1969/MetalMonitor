using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.Core
{
    public class Device : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private double _temperature;
        private bool _isAlarming;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public double Temperature
        {
            get => _temperature;
            set
            {
                if (SetProperty(ref _temperature, value))
                {
                    // 温度变化时，自动判断是否报警
                    IsAlarming = value >= 85;
                }
            }
        }

        public bool IsAlarming
        {
            get => _isAlarming;
            private set => SetProperty(ref _isAlarming, value);
        }

        // 构造一个方便测试的设备
        public Device(string name, double initialTemp = 60)
        {
            Name = name;
            _temperature = initialTemp;
            IsAlarming = initialTemp >= 85;
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