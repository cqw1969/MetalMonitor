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
            set => SetProperty(ref _temperature, value);
        }

        public bool IsAlarming
        {
            get => _isAlarming;
            private set => SetProperty(ref _isAlarming, value);
        }

        // 设置报警状态（由服务层调用）
        public void SetAlarmState(bool isAlarming)
        {
            IsAlarming = isAlarming;
        }

        // 构造一个方便测试的设备
        public Device(string name, double initialTemp = 60)
        {
            Name = name;
            _temperature = initialTemp;//这里直接赋值给字段而不是属性，以避免触发 PropertyChanged 事件
            IsAlarming = false;
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