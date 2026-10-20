using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 报警方向：指标值向哪个方向越限算报警。
    /// </summary>
    public enum AlarmDirection
    {
        /// <summary>高于阈值报警（如温度超限）</summary>
        Above,

        /// <summary>低于阈值报警（如压力过低）</summary>
        Below
    }

    /// <summary>
    /// 指标配置实体：描述一种指标类型（温度/电压/湿度…）的显示名、单位、量程与报警规则，
    /// 对应数据库 MetricConfigs 表的一行。DeviceService 用它驱动模拟数据生成与报警判定。
    /// </summary>
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

        /// <summary>自增主键（数据库分配）</summary>
        public int Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        /// <summary>指标内部名（如 Temperature/Voltage）；数据通路与报警字典以此为键，不可随意更改</summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>界面显示名（如"温度""电压"）</summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        /// <summary>计量单位字符串（如 ℃、V、%、MPa、rpm），随数据记录落库并在图表轴显示</summary>
        public string Unit
        {
            get => _unit;
            set => SetProperty(ref _unit, value);
        }

        /// <summary>报警阈值；配合 <see cref="AlarmDirection"/> 判定越限方向</summary>
        public double AlarmThreshold
        {
            get => _alarmThreshold;
            set => SetProperty(ref _alarmThreshold, value);
        }

        /// <summary>是否启用该指标的报警判定；false 时只采集不报警</summary>
        public bool AlarmEnabled
        {
            get => _alarmEnabled;
            set => SetProperty(ref _alarmEnabled, value);
        }

        /// <summary>报警方向（高于/低于阈值）</summary>
        public AlarmDirection AlarmDirection
        {
            get => _alarmDirection;
            set => SetProperty(ref _alarmDirection, value);
        }

        /// <summary>模拟数据量程下限（同时作为随机游走钳制边界）</summary>
        public double MinValue
        {
            get => _minValue;
            set => SetProperty(ref _minValue, value);
        }

        /// <summary>模拟数据量程上限（默认 100）</summary>
        public double MaxValue
        {
            get => _maxValue;
            set => SetProperty(ref _maxValue, value);
        }

        /// <summary>该指标是否参与采集；false 时心跳循环跳过并清除其报警状态</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        /// <summary>属性变更事件（INotifyPropertyChanged）：设置界面修改配置时实时刷新绑定</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 属性设置器通用封装：值变化时写字段并触发 PropertyChanged，值相同则不触发。
        /// </summary>
        /// <typeparam name="T">属性类型</typeparam>
        /// <param name="field">后备字段引用</param>
        /// <param name="value">新值</param>
        /// <param name="propertyName">属性名，由 CallerMemberName 自动填充</param>
        /// <returns>true=值已变化并通知；false=新旧值相等</returns>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// 触发 PropertyChanged 事件。
        /// </summary>
        /// <param name="propertyName">变更的属性名；null 时由 CallerMemberName 自动填充</param>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
