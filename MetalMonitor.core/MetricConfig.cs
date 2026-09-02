using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.Core
{
    public enum AlarmDirection
    {
        Above,  // 超过阈值报警
        Below   // 低于阈值报警
    }

    public class MetricConfig : INotifyPropertyChanged
    {
        private int _id;
        private string _name = string.Empty;
        private string _displayName = string.Empty;
        private string _unit = string.Empty;
        private double _alarmThreshold;
        private bool _alarmEnabled = true;
        private AlarmDirection _alarmDirection = AlarmDirection.Above;
        private double _minValue;
        private double _maxValue = 100;
        private bool _isActive = true;

        public int Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        public string Unit
        {
            get => _unit;
            set => SetProperty(ref _unit, value);
        }

        public double AlarmThreshold
        {
            get => _alarmThreshold;
            set => SetProperty(ref _alarmThreshold, value);
        }

        public bool AlarmEnabled
        {
            get => _alarmEnabled;
            set => SetProperty(ref _alarmEnabled, value);
        }

        public AlarmDirection AlarmDirection
        {
            get => _alarmDirection;
            set => SetProperty(ref _alarmDirection, value);
        }

        public double MinValue
        {
            get => _minValue;
            set => SetProperty(ref _minValue, value);
        }

        public double MaxValue
        {
            get => _maxValue;
            set => SetProperty(ref _maxValue, value);
        }

        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

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
