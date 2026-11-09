using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MetalMonitor.Core;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MetalMonitor.UI.ViewModels
{
    /// <summary>
    /// 单台设备的卡片视图模型：订阅所属 <see cref="Device"/> 的属性与指标值变更事件，
    /// 维护按指标分别存储的历史曲线点与报警散点（LiveCharts），驱动卡片数值、报警变红、
    /// 停用置灰与暂停遮罩等显示状态；释放时须退订设备事件。
    /// </summary>
    public class DeviceCardViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>本卡片绑定的设备领域对象。</summary>
        public Device Device { get; }
        /// <summary>设备数据服务，用于读取指标配置与历史点保留上限。</summary>
        private readonly IDeviceService _deviceService;

        /// <summary>卡片当前展示的指标名称，由主界面下拉框统一切换，默认 Temperature。</summary>
        private string _metricName = "Temperature";

        /// <summary>每个指标独立的历史曲线点集合（按指标名索引）。</summary>
        // 每个指标独立的历史点/报警点集合
        private readonly Dictionary<string, ObservableCollection<ObservablePoint>> _pointsByMetric = new();
        /// <summary>每个指标独立的报警散点集合（按指标名索引）。</summary>
        private readonly Dictionary<string, ObservableCollection<ObservablePoint>> _alarmPointsByMetric = new();

        /// <summary>当前绑定到图表的历史曲线系列实例。</summary>
        private LineSeries<ObservablePoint> _lineSeries;
        /// <summary>当前绑定到图表的报警散点系列实例。</summary>
        private ScatterSeries<ObservablePoint> _alarmSeries;

        /// <summary>图表系列数组（曲线 + 报警散点），切换指标时整体替换。</summary>
        public ISeries[] Series { get; private set; }
        /// <summary>图表 X 轴配置（卡片上隐藏不显示）。</summary>
        public Axis[] XAxes { get; set; }
        /// <summary>图表 Y 轴配置（范围随指标最值刷新，卡片上隐藏不显示）。</summary>
        public Axis[] YAxes { get; set; }

        /// <summary>
        /// 初始化 <see cref="DeviceCardViewModel"/> 的新实例：订阅设备事件、创建曲线与报警系列、
        /// 初始化坐标轴并按当前指标刷新卡片显示。
        /// </summary>
        /// <param name="device">卡片对应的设备。</param>
        /// <param name="deviceService">设备数据服务。</param>
        /// <remarks>
        /// 构造时订阅 <see cref="Device.MetricValueChanged"/> 与 <see cref="Device.PropertyChanged"/>，
        /// 必须在 <see cref="Dispose(bool)"/> 中对应退订以避免事件泄漏。
        /// </remarks>
        public DeviceCardViewModel(Device device, IDeviceService deviceService)
        {
            Device = device;
            _deviceService = deviceService;

            Device.MetricValueChanged += OnMetricValueChanged;
            Device.PropertyChanged += OnDevicePropertyChanged;
            Device.MetricQualityChanged += OnMetricQualityChanged;

            _lineSeries = CreateLineSeries();
            _alarmSeries = CreateAlarmSeries();
            Series = new ISeries[] { _lineSeries, _alarmSeries };

            XAxes = new Axis[]
            {
                new Axis
                {
                    ShowSeparatorLines = false,
                    IsVisible = false
                }
            };

            YAxes = new Axis[]
            {
                new Axis
                {
                    ShowSeparatorLines = false,
                    IsVisible = false
                }
            };

            RefreshMetricDisplay();
        }

        /// <summary>
        /// 创建绿色半透明填充的历史折线系列。
        /// </summary>
        /// <returns>配置好的 <see cref="LineSeries{ObservablePoint}"/> 新实例。</returns>
        private LineSeries<ObservablePoint> CreateLineSeries() => new()
        {
            Fill = new SolidColorPaint(new SKColor(108, 212, 108, 80)),
            Stroke = new SolidColorPaint(SKColors.LightGreen) { StrokeThickness = 2 },
            GeometrySize = 0,
            LineSmoothness = 0.4
        };

        /// <summary>
        /// 创建红色的报警散点系列。
        /// </summary>
        /// <returns>配置好的 <see cref="ScatterSeries{ObservablePoint}"/> 新实例。</returns>
        private ScatterSeries<ObservablePoint> CreateAlarmSeries() => new()
        {
            GeometrySize = 5,
            Fill = new SolidColorPaint(SKColors.Red),
            Stroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 1 }
        };

        /// <summary>
        /// 获取或设置当前卡片展示的指标名（由主界面下拉框切换）；变更后重建图表显示。
        /// </summary>
        // 当前卡片展示的指标名（由主界面下拉框切换）
        public string MetricName
        {
            get => _metricName;
            set
            {
                if (_metricName == value) return;
                _metricName = value;
                OnPropertyChanged();
                RefreshMetricDisplay();
            }
        }

        /// <summary>获取当前指标对应的配置；配置不存在时为 null。</summary>
        private MetricConfig? CurrentConfig =>
            _deviceService.GetMetricConfigs().FirstOrDefault(c => c.Name == _metricName);

        /// <summary>获取当前指标的显示名称；无配置时回退为指标名本身。</summary>
        public string MetricDisplayName => CurrentConfig?.DisplayName ?? _metricName;

        /// <summary>获取当前指标的单位文本；无配置时为空字符串。</summary>
        public string CurrentUnit => CurrentConfig?.Unit ?? string.Empty;

        /// <summary>获取当前指标是否启用（停用后卡片置灰）。</summary>
        // 指标是否启用（停用后卡片置灰）
        public bool IsMetricActive => CurrentConfig?.IsActive ?? false;

        /// <summary>获取设备是否处于暂停/停止状态（<see cref="Device.IsActive"/> 为 false），暂停时卡片整体灰化并显示“暂停中”。</summary>
        // 设备是否处于暂停/停止状态（IsActive=false），暂停时卡片整体灰化并显示"暂停中"
        public bool IsPaused => !Device.IsActive;

        /// <summary>获取当前指标是否处于报警状态（驱动卡片红色报警样式）。</summary>
        // 当前指标是否处于报警状态
        public bool IsMetricAlarming => Device.IsMetricAlarming(_metricName);

        /// <summary>获取当前指标的数据质量码（良好/陈旧/不可信），驱动数值颜色与质量角标；从未写过为 Bad。</summary>
        public MetricQuality CurrentMetricQuality => Device.GetMetricQuality(_metricName);

        /// <summary>
        /// 获取数值显示文本：指标停用或无数值（NaN）时显示“--”，否则保留一位小数。
        /// </summary>
        // 数值显示文本：停用无数值时显示 "--"
        public string ValueText
        {
            get
            {
                if (!IsMetricActive) return "--";
                double v = Device.GetMetricValue(_metricName);
                return double.IsNaN(v) ? "--" : v.ToString("F1");
            }
        }

        /// <summary>
        /// 切换/刷新当前指标的图表显示：重建曲线与报警系列实例并绑定当前指标的数据点，
        /// 刷新坐标范围、颜色与各绑定属性。
        /// </summary>
        /// <remarks>重建系列实例可避免复用旧 <see cref="ScatterSeries{ObservablePoint}"/> 导致上一指标的红色报警点残留。</remarks>
        // 切换指标：重建系列实例并绑定当前指标的数据点，
        // 避免复用旧 ScatterSeries 导致上一指标的红色报警点残留
        public void RefreshMetricDisplay()
        {
            _lineSeries = CreateLineSeries();
            _alarmSeries = CreateAlarmSeries();
            _lineSeries.Values = GetPoints(_metricName);
            _alarmSeries.Values = GetAlarmPoints(_metricName);
            Series = new ISeries[] { _lineSeries, _alarmSeries };

            ApplyMetricLimits();
            ApplyChartColors();

            OnPropertyChanged(nameof(Series));
            OnPropertyChanged(nameof(MetricDisplayName));
            OnPropertyChanged(nameof(CurrentUnit));
            OnPropertyChanged(nameof(IsMetricActive));
            OnPropertyChanged(nameof(IsMetricAlarming));
            OnPropertyChanged(nameof(CurrentMetricQuality));
            OnPropertyChanged(nameof(ValueText));
        }

        /// <summary>
        /// 按当前指标配置刷新卡片 Y 轴上下限、曲线颜色及数值/报警/启用绑定状态。
        /// </summary>
        /// <remarks>指标配置可能在系统设置中被修改，每秒由主界面定时器调用刷新。</remarks>
        // 配置可能在系统设置中被修改，每秒由主界面调用刷新
        public void ApplyMetricLimits()
        {
            var config = CurrentConfig;
            if (config != null)
            {
                YAxes[0].MinLimit = config.MinValue;
                YAxes[0].MaxLimit = config.MaxValue;
            }
            ApplyChartColors();
            OnPropertyChanged(nameof(IsMetricActive));
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(IsMetricAlarming));
        }

        /// <summary>
        /// 按当前指标启用状态应用曲线配色：启用为绿色曲线，停用为灰色曲线。
        /// </summary>
        // 启用=绿色曲线；停用=灰色曲线
        private void ApplyChartColors()
        {
            if (IsMetricActive)
            {
                _lineSeries.Fill = new SolidColorPaint(new SKColor(108, 212, 108, 80));
                _lineSeries.Stroke = new SolidColorPaint(SKColors.LightGreen) { StrokeThickness = 2 };
            }
            else
            {
                _lineSeries.Fill = new SolidColorPaint(new SKColor(130, 130, 140, 60));
                _lineSeries.Stroke = new SolidColorPaint(new SKColor(150, 150, 160)) { StrokeThickness = 2 };
            }
        }

        /// <summary>
        /// 获取指定指标的历史曲线点集合，不存在时按需创建并缓存。
        /// </summary>
        /// <param name="metric">指标名称。</param>
        /// <returns>该指标对应的历史曲线点集合。</returns>
        private ObservableCollection<ObservablePoint> GetPoints(string metric)
        {
            if (!_pointsByMetric.TryGetValue(metric, out var col))
            {
                col = new ObservableCollection<ObservablePoint>();
                _pointsByMetric[metric] = col;
            }
            return col;
        }

        /// <summary>
        /// 获取指定指标的报警散点集合，不存在时按需创建并缓存。
        /// </summary>
        /// <param name="metric">指标名称。</param>
        /// <returns>该指标对应的报警散点集合。</returns>
        private ObservableCollection<ObservablePoint> GetAlarmPoints(string metric)
        {
            if (!_alarmPointsByMetric.TryGetValue(metric, out var col))
            {
                col = new ObservableCollection<ObservablePoint>();
                _alarmPointsByMetric[metric] = col;
            }
            return col;
        }

        /// <summary>
        /// 向指定指标追加一个历史点；若该值处于报警状态则同步追加报警散点，
        /// 并在点数超过服务端配置上限时滚动移除最旧数据、重排 X 坐标。
        /// </summary>
        /// <param name="metricName">指标名称。</param>
        /// <param name="value">本次指标值。</param>
        private void AddMetricPoint(string metricName, double value)
        {
            var points = GetPoints(metricName);
            points.Add(new ObservablePoint(points.Count, value));

            if (Device.IsMetricAlarming(metricName))
            {
                GetAlarmPoints(metricName).Add(new ObservablePoint(points.Count - 1, value));
            }

            while (points.Count > _deviceService.MaxHistoryPoints)
            {
                points.RemoveAt(0);
                for (int i = 0; i < points.Count; i++)
                    points[i].X = i;

                var alarms = GetAlarmPoints(metricName);
                for (int i = alarms.Count - 1; i >= 0; i--)
                {
                    alarms[i].X -= 1;
                    if (alarms[i].X < 0)
                        alarms.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 设备指标值更新事件处理：追加历史点并在该指标为当前展示指标时刷新数值与报警状态。
        /// </summary>
        /// <param name="metricName">更新值的指标名称。</param>
        /// <param name="value">最新指标值。</param>
        /// <remarks>
        /// 该事件由定时器所在的后台线程触发，非 UI 线程时通过 Dispatcher 异步封送回 UI 线程执行，
        /// 保证 LiveCharts 集合与绑定属性只在 UI 线程更新。
        /// </remarks>
        // 设备指标值更新（定时器线程触发，需切回 UI 线程）
        private void OnMetricValueChanged(string metricName, double value)
        {
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(() => OnMetricValueChanged(metricName, value));
                return;
            }

            AddMetricPoint(metricName, value);

            if (metricName == _metricName)
            {
                OnPropertyChanged(nameof(ValueText));
                OnPropertyChanged(nameof(IsMetricAlarming));
            }
        }

        /// <summary>
        /// 设备指标质量变更处理：仅当变更的是当前展示指标时刷新质量属性，驱动数值颜色与角标。
        /// </summary>
        /// <param name="metricName">质量变更的指标名称。</param>
        /// <param name="quality">新的质量码（良好/陈旧/不可信）。</param>
        /// <remarks>
        /// 事件由心跳降级扫描或写值路径触发（后台线程），非 UI 线程时通过 Dispatcher 异步封送；
        /// Core 侧同值短路仅跃迁触发。切指标瞬间被旧名过滤的事件无害——切换本身已在
        /// <see cref="RefreshMetricDisplay"/> 中刷新质量属性。
        /// </remarks>
        // 设备指标质量变更（心跳线程触发，需切回 UI 线程）
        private void OnMetricQualityChanged(string metricName, MetricQuality quality)
        {
            if (metricName != _metricName) return;

            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(() => OnMetricQualityChanged(metricName, quality));
                return;
            }

            OnPropertyChanged(nameof(CurrentMetricQuality));
        }

        /// <summary>
        /// 设备通用属性变更处理：报警总状态变化时刷新报警属性，启用状态变化时刷新暂停遮罩属性。
        /// </summary>
        /// <param name="sender">事件发送者（设备对象），可能为 null。</param>
        /// <param name="e">属性变更事件参数，<see cref="PropertyChangedEventArgs.PropertyName"/> 标识变更属性。</param>
        /// <remarks>事件可能来自后台线程，通知统一通过 Dispatcher 封送回 UI 线程。</remarks>
        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Device.IsAlarming))
            {
                Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsMetricAlarming)));
            }
            else if (e.PropertyName == nameof(Device.IsActive))
            {
                // 设备启用/停止状态变化：通知遮罩层显示或隐藏
                Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsPaused)));
            }
        }

        /// <summary>属性变更通知事件，供 WPF 绑定系统订阅。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件；属性名缺省时由调用方成员名自动填充。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称，null 时由 <see cref="CallerMemberNameAttribute"/> 自动传入。</param>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region IDisposable

        /// <summary>释放标记：true 表示已执行过释放逻辑，防止重复释放。</summary>
        private bool _disposed = false;

        /// <summary>释放本视图模型：退订设备的指标值与属性变更事件，避免事件引用导致卡片无法回收。</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 受保护的释放实现：保证释放幂等，显式释放时退订两个设备事件。
        /// </summary>
        /// <param name="disposing">true 表示由 <see cref="Dispose"/> 显式调用；false 表示由终结器调用。</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Device.MetricValueChanged -= OnMetricValueChanged;
                Device.PropertyChanged -= OnDevicePropertyChanged;
                Device.MetricQualityChanged -= OnMetricQualityChanged;
            }

            _disposed = true;
        }

        /// <summary>终结器：未显式释放时以 disposing=false 兜底（退订逻辑仅在显式释放路径执行）。</summary>
        ~DeviceCardViewModel()
        {
            Dispose(false);
        }

        #endregion
    }
}
