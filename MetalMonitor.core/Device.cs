using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 设备实体 + 变更广播中枢：对应数据库 Devices 表的一行，同时用
    /// MetricValueChanged / PropertyChanged 事件把值变化实时广播给 UI 卡片。
    /// 指标值与报警状态用 ConcurrentDictionary——心跳/MQTT/串口三个线程来源都会写入。
    /// </summary>
    public class Device : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _name = string.Empty;
        private string _deviceType = string.Empty;
        private string _location = string.Empty;
        private string _description = string.Empty;
        private bool _isActive = true;
        private bool _isAlarming;
        private DateTime _createdAt;

        /// <summary>各指标的当前值（指标名 -&gt; 值）；并发字典，供心跳/MQTT/串口三个线程来源安全写入</summary>
        private readonly ConcurrentDictionary<string, double> _metricValues = new();

        /// <summary>各指标的报警状态（指标名 -&gt; 是否报警）；任一指标报警则设备整体报警</summary>
        private readonly ConcurrentDictionary<string, bool> _metricAlarms = new();

        /// <summary>各指标最近一次写入值的单调毫秒时刻（指标名 -&gt; TickCount64）；0/缺失表示从未写入</summary>
        private readonly ConcurrentDictionary<string, long> _metricValueMs = new();

        /// <summary>各指标当前的数据质量码（指标名 -&gt; 质量）；缺失按 <see cref="MetricQuality.Bad"/> 解释</summary>
        private readonly ConcurrentDictionary<string, MetricQuality> _metricQuality = new();

        /// <summary>质量码跃迁串行化锁：保证"比较旧值 + 写入 + 判定是否通知"原子，避免重复通知</summary>
        private readonly object _qualityLock = new();

        /// <summary>指标值更新事件：参数依次为指标名、最新值；由 SetMetricValue 在写入后触发</summary>
        public event Action<string, double>? MetricValueChanged;

        /// <summary>指标质量码跃迁事件：参数依次为指标名、新质量码；仅在实际变化时触发</summary>
        public event Action<string, MetricQuality>? MetricQualityChanged;

        /// <summary>设备唯一标识（Guid 字符串），数据库主键，同步设备时以此判同</summary>
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        /// <summary>设备名称（展示与 MQTT 消息匹配用，如"1号冲压机"）</summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>设备类型（自由文本，如冲压机/加热炉）</summary>
        public string DeviceType
        {
            get => _deviceType;
            set => SetProperty(ref _deviceType, value);
        }

        /// <summary>安装位置（自由文本，用于卡片与详情展示）</summary>
        public string Location
        {
            get => _location;
            set => SetProperty(ref _location, value);
        }

        /// <summary>备注描述（可为空字符串）</summary>
        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        /// <summary>是否启用；false 时三条数据通路均跳过该设备（不更新/不报警/不落库）</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        /// <summary>设备整体是否报警（任一指标报警即为 true）；仅服务层可通过 SetMetricAlarmState 间接修改</summary>
        public bool IsAlarming
        {
            get => _isAlarming;
            private set => SetProperty(ref _isAlarming, value);
        }

        /// <summary>设备创建时间（本地时间），构造时写入</summary>
        public DateTime CreatedAt
        {
            get => _createdAt;
            set => SetProperty(ref _createdAt, value);
        }

        /// <summary>
        /// 设置设备整体报警状态（由服务层调用）。
        /// </summary>
        /// <param name="isAlarming">true=报警中（卡片变红）；false=正常</param>
        public void SetAlarmState(bool isAlarming)
        {
            IsAlarming = isAlarming;
        }

        /// <summary>
        /// 写入某指标的当前值并广播变更（由服务层"三步汇合点"调用）。
        /// </summary>
        /// <param name="metricName">指标名（如 Temperature/Voltage），须与 MetricConfig.Name 一致</param>
        /// <param name="value">最新指标值（double，已换算为物理量单位）</param>
        /// <remarks>写入后依次触发 MetricValueChanged 与 MetricValues 的 PropertyChanged，驱动 UI 曲线/卡片刷新</remarks>
        public void SetMetricValue(string metricName, double value)
        {
            _metricValues[metricName] = value;
            _metricValueMs[metricName] = Environment.TickCount64;   // 记录新鲜度时刻（单调时钟）
            SetMetricQuality(metricName, MetricQuality.Good);        // 写值瞬间即恢复可信，零延迟
            MetricValueChanged?.Invoke(metricName, value);
            OnPropertyChanged(nameof(MetricValues));
        }

        /// <summary>
        /// 读取某指标当前值。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <returns>当前值；该指标从未写入过则返回 <see cref="double.NaN"/></returns>
        public double GetMetricValue(string metricName)
        {
            return _metricValues.TryGetValue(metricName, out var v) ? v : double.NaN;
        }

        /// <summary>
        /// 读取某指标最近一次写入值的单调毫秒时刻。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <returns>写入时刻（TickCount64）；从未写入过返回 0</returns>
        public long GetMetricValueMs(string metricName)
        {
            return _metricValueMs.TryGetValue(metricName, out var ms) ? ms : 0;
        }

        /// <summary>
        /// 写入某指标的数据质量码（由服务层心跳的降级扫描调用）。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <param name="quality">新的质量码</param>
        /// <remarks>同值不触发 <see cref="MetricQualityChanged"/>，避免每秒心跳造成通知风暴</remarks>
        public void SetMetricQuality(string metricName, MetricQuality quality)
        {
            lock (_qualityLock)
            {
                if (_metricQuality.TryGetValue(metricName, out var existing) && existing == quality)
                    return;
                _metricQuality[metricName] = quality;
            }
            // 事件在锁外触发：订阅方回调不得延长持锁时间，也避免重入死锁
            MetricQualityChanged?.Invoke(metricName, quality);
        }

        /// <summary>
        /// 读取某指标当前的数据质量码。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <returns>当前质量码；从未写入过视为 <see cref="MetricQuality.Bad"/></returns>
        public MetricQuality GetMetricQuality(string metricName)
        {
            return _metricQuality.TryGetValue(metricName, out var q) ? q : MetricQuality.Bad;
        }

        /// <summary>所有指标当前值的只读快照（供 UI 绑定）；底层为实时字典，读取时反映最新内容</summary>
        public IReadOnlyDictionary<string, double> MetricValues => _metricValues;

        /// <summary>
        /// 写入某指标的报警状态，并重新汇总设备整体报警状态。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <param name="isAlarming">该指标是否处于报警状态</param>
        /// <remarks>任一指标报警则 <see cref="IsAlarming"/> 为 true；必须先于 SetMetricValue 调用，避免 UI 读到"新值+旧状态"</remarks>
        public void SetMetricAlarmState(string metricName, bool isAlarming)
        {
            _metricAlarms[metricName] = isAlarming;
            IsAlarming = _metricAlarms.Values.Any(a => a);
        }

        /// <summary>
        /// 查询单个指标是否处于报警状态。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <returns>存在且报警为 true；不存在或未报警均为 false</returns>
        public bool IsMetricAlarming(string metricName)
        {
            return _metricAlarms.TryGetValue(metricName, out var a) && a;
        }

        /// <summary>无参构造函数：自动生成 Guid 与创建时间（用于数据库读取后属性赋值等场景）</summary>
        public Device()
        {
            Id = Guid.NewGuid().ToString();
            _createdAt = DateTime.Now;
        }

        /// <summary>
        /// 仅指定名称的构造函数（测试与快速建实例用）。
        /// </summary>
        /// <param name="name">设备名称</param>
        public Device(string name)
        {
            Id = Guid.NewGuid().ToString();
            Name = name;
            IsAlarming = false;
            _createdAt = DateTime.Now;
        }

        /// <summary>
        /// 设备管理界面创建设备用的完整构造函数。
        /// </summary>
        /// <param name="name">设备名称</param>
        /// <param name="deviceType">设备类型</param>
        /// <param name="location">安装位置</param>
        /// <param name="description">备注描述，默认空字符串</param>
        public Device(string name, string deviceType, string location, string description = "")
        {
            Id = Guid.NewGuid().ToString();
            Name = name;
            _deviceType = deviceType;
            _location = location;
            _description = description;
            _isActive = true;
            IsAlarming = false;
            _createdAt = DateTime.Now;
        }

        /// <summary>属性变更事件（INotifyPropertyChanged）：WPF 数据绑定据此自动刷新 UI</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 属性设置器通用封装：值变化时写字段并触发 PropertyChanged，值相同则不触发。
        /// </summary>
        /// <typeparam name="T">属性类型</typeparam>
        /// <param name="field">属性对应后备字段引用</param>
        /// <param name="value">新值</param>
        /// <param name="propertyName">属性名，由编译器通过 CallerMemberName 自动填充</param>
        /// <returns>true=值确实发生变化并已通知；false=新旧值相等被忽略</returns>
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
