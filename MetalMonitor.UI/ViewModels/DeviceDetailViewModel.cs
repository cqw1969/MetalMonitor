using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MetalMonitor.Core;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MetalMonitor.UI.ViewModels
{
    /// <summary>
    /// 设备详情窗口视图模型：按设备、指标与时间范围从数据库查询历史数据并以 LiveCharts 曲线展示，
    /// 同时订阅设备实时指标事件在曲线头部追加实时点；窗口重新激活时刷新指标配置。
    /// </summary>
    public class DeviceDetailViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>本详情页对应的设备。</summary>
        private readonly Device _device;
        /// <summary>数据库服务，用于查询指标配置与历史数据记录。</summary>
        private readonly IDatabaseService _databaseService;
        /// <summary>历史查询起始时间，默认今天零点。</summary>
        private DateTime _startDate = DateTime.Today;
        /// <summary>历史查询结束时间，默认当前时刻。</summary>
        private DateTime _endDate = DateTime.Now;
        /// <summary>当前选中的查询/展示指标名称，默认 Temperature。</summary>
        private string _selectedMetricName = "Temperature";
        /// <summary>是否正在加载历史数据（驱动等待态显示）。</summary>
        private bool _isLoading = false;
        /// <summary>从数据库加载的全部指标配置缓存（含停用指标）。</summary>
        private List<MetricConfig> _metricConfigs = new();

        /// <summary>属性变更通知事件，供 WPF 绑定系统订阅。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>获取本详情页对应的设备。</summary>
        public Device Device => _device;

        /// <summary>
        /// 获取或设置历史查询起始时间；变更时通知 UI。
        /// </summary>
        public DateTime StartDate
        {
            get => _startDate;
            set { _startDate = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 获取或设置历史查询结束时间；变更时通知 UI。
        /// </summary>
        public DateTime EndDate
        {
            get => _endDate;
            set { _endDate = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 获取或设置当前选中的指标名称；变更后刷新名称/单位显示、头部实时值并重载历史曲线。
        /// </summary>
        /// <remarks>拒绝 null/空值（删除指标配置后 ComboBox 选中项可能变 null）。</remarks>
        public string SelectedMetricName
        {
            get => _selectedMetricName;
            set
            {
                // 拒绝 null/空值（删除指标配置后 ComboBox 选中项可能变 null）
                if (string.IsNullOrEmpty(value)) return;
                if (_selectedMetricName == value) return;
                _selectedMetricName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MetricDisplayName));
                OnPropertyChanged(nameof(CurrentMetricUnit));
                UpdateCurrentValueText();
                LoadHistoricalDataAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 获取或设置历史数据加载状态；变更时通知 UI。
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        /// <summary>当前时间范围查询得到的历史数据记录集合（绑定明细表格）。</summary>
        public ObservableCollection<DataRecord> HistoricalRecords { get; } = new();
        /// <summary>可选指标配置集合（仅含启用指标，绑定指标下拉框）。</summary>
        public ObservableCollection<MetricConfig> AvailableMetrics { get; } = new();

        /// <summary>历史曲线系列数组（单条绿色折线）。</summary>
        public ISeries[] Series { get; set; }
        /// <summary>图表 X 轴配置（按 OADate 时间显示 HH:mm 标签）。</summary>
        public Axis[] XAxes { get; set; }
        /// <summary>图表 Y 轴配置（范围取指标最值，标签带单位）。</summary>
        public Axis[] YAxes { get; set; }

        /// <summary>图表实际绑定的曲线点集合（历史点与实时点共用，随查询清空重建）。</summary>
        private ObservableCollection<ObservablePoint> _chartPoints = new();

        /// <summary>头部实时值显示文本 backing field，初始“--”。</summary>
        // 头部实时值显示
        private string _currentValueText = "--";
        /// <summary>
        /// 获取当前指标的头部实时值显示文本；仅类内部可写。
        /// </summary>
        public string CurrentValueText
        {
            get => _currentValueText;
            private set { _currentValueText = value; OnPropertyChanged(); }
        }

        /// <summary>获取当前选中指标的显示名称；无配置时回退为指标名本身。</summary>
        public string MetricDisplayName
        {
            get
            {
                var config = _metricConfigs.FirstOrDefault(c => c.Name == _selectedMetricName);
                return config?.DisplayName ?? _selectedMetricName;
            }
        }

        /// <summary>获取当前选中指标的单位文本；无配置时为空字符串。</summary>
        public string CurrentMetricUnit
        {
            get
            {
                var config = _metricConfigs.FirstOrDefault(c => c.Name == _selectedMetricName);
                return config?.Unit ?? "";
            }
        }

        /// <summary>
        /// 初始化 <see cref="DeviceDetailViewModel"/> 的新实例：保存设备与数据库服务、
        /// 订阅设备实时指标事件、构建历史曲线系列与坐标轴，并异步加载指标配置与历史数据。
        /// </summary>
        /// <param name="device">详情对应的设备。</param>
        /// <param name="databaseService">数据库服务。</param>
        /// <remarks>
        /// 构造时订阅 <see cref="Device.MetricValueChanged"/>，窗口关闭时由 <see cref="Dispose"/> 退订；
        /// X 轴以 OADate（天）为单位，标签最小间隔 2 分钟。
        /// </remarks>
        public DeviceDetailViewModel(Device device, IDatabaseService databaseService)
        {
            _device = device;
            _databaseService = databaseService;

            _device.MetricValueChanged += OnMetricValueChanged;

            _chartPoints = new ObservableCollection<ObservablePoint>();

            Series = new ISeries[]
            {
                new LineSeries<ObservablePoint>
                {
                    Values = _chartPoints,
                    Fill = new SolidColorPaint(new SKColor(108, 212, 108, 80)),
                    Stroke = new SolidColorPaint(SKColors.LightGreen) { StrokeThickness = 2 },
                    GeometrySize = 4,
                    LineSmoothness = 0.4
                }
            };

            XAxes = new Axis[]
            {
                new Axis
                {
                    ShowSeparatorLines = true,
                    Labeler = value =>
                    {
                        var timestamp = DateTime.FromOADate(value);
                        return timestamp.ToString("HH:mm");
                    },
                    // X 轴标签最小间隔 2 分钟（OADate 单位为天），避免标签糊成一团
                    MinStep = 2.0 / (24 * 60)
                }
            };

            YAxes = new Axis[]
            {
                new Axis
                {
                    ShowSeparatorLines = true,
                    Labeler = value => $"{value:F1} {GetMetricUnit()}"
                }
            };

            LoadMetricsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 从数据库加载全部指标配置，刷新启用指标下拉列表（当前选中指标失效时回退到第一个），随后加载历史数据。
        /// </summary>
        /// <returns>表示异步加载操作的任务。</returns>
        private async Task LoadMetricsAsync()
        {
            var configs = await _databaseService.GetAllMetricConfigsAsync();
            _metricConfigs = configs;
            AvailableMetrics.Clear();
            foreach (var c in configs)
            {
                if (c.IsActive)
                    AvailableMetrics.Add(c);
            }
            if (AvailableMetrics.Count > 0 && !AvailableMetrics.Any(c => c.Name == _selectedMetricName))
                _selectedMetricName = AvailableMetrics[0].Name;

            OnPropertyChanged(nameof(SelectedMetricName));
            OnPropertyChanged(nameof(MetricDisplayName));
            OnPropertyChanged(nameof(CurrentMetricUnit));

            await LoadHistoricalDataAsync();
        }

        /// <summary>
        /// 窗口重新激活时重新从数据库加载指标配置（系统设置中改过阈值/最值后生效），
        /// 当前指标被停用/删除时回退到第一个可用指标，否则仅刷新显示与曲线。
        /// </summary>
        /// <returns>表示异步刷新操作的任务。</returns>
        // 窗口重新激活时刷新配置（系统设置中改过阈值/最值后生效）
        public async Task RefreshConfigsAsync()
        {
            var configs = await _databaseService.GetAllMetricConfigsAsync();
            _metricConfigs = configs;
            AvailableMetrics.Clear();
            foreach (var c in configs)
            {
                if (c.IsActive)
                    AvailableMetrics.Add(c);
            }

            // 当前指标被停用/删除时回退到第一个可用指标
            if (!AvailableMetrics.Any(c => c.Name == _selectedMetricName))
            {
                if (AvailableMetrics.Count > 0)
                {
                    SelectedMetricName = AvailableMetrics[0].Name;
                    return; // setter 会触发历史数据重载
                }
            }

            OnPropertyChanged(nameof(MetricDisplayName));
            OnPropertyChanged(nameof(CurrentMetricUnit));
            UpdateCurrentValueText();
            UpdateChart();
        }

        /// <summary>
        /// 按当前设备、选中指标与起止时间从数据库查询历史记录，填充明细集合并重绘曲线、刷新头部实时值。
        /// </summary>
        /// <returns>表示异步加载操作的任务。</returns>
        /// <remarks>加载期间 <see cref="IsLoading"/> 为 true；查询异常仅输出到控制台，不向上抛出。</remarks>
        public async Task LoadHistoricalDataAsync()
        {
            IsLoading = true;
            HistoricalRecords.Clear();
            _chartPoints.Clear();

            try
            {
                var records = await _databaseService.GetDataRecordsByTimeRangeAsync(
                    StartDate,
                    EndDate,
                    _device.Id,
                    SelectedMetricName
                );

                foreach (var record in records)
                {
                    HistoricalRecords.Add(record);
                }

                UpdateChart();
                UpdateCurrentValueText();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载历史数据失败: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 依据 <see cref="HistoricalRecords"/> 重建曲线点，按数据首尾时间设置 X 轴范围，
        /// 按指标配置最值设置 Y 轴范围与带单位的标签格式。
        /// </summary>
        private void UpdateChart()
        {
            _chartPoints.Clear();

            foreach (var record in HistoricalRecords)
            {
                _chartPoints.Add(new ObservablePoint(record.Timestamp.ToOADate(), record.Value));
            }

            if (_chartPoints.Count > 0)
            {
                XAxes[0].MinLimit = _chartPoints[0].X;
                XAxes[0].MaxLimit = _chartPoints[_chartPoints.Count - 1].X;
            }

            // Y 轴范围与指标设置中的最值一致
            var (min, max) = GetMetricMinMax();
            YAxes[0].MinLimit = min;
            YAxes[0].MaxLimit = max;
            YAxes[0].Labeler = value => $"{value:F1} {GetMetricUnit()}";
            OnPropertyChanged(nameof(YAxes));
        }

        /// <summary>
        /// 获取当前选中指标配置的 Y 轴上下限；配置不存在时返回默认范围 (0, 100)。
        /// </summary>
        /// <returns>以最小值、最大值组成的元组。</returns>
        private (double min, double max) GetMetricMinMax()
        {
            var config = _metricConfigs.FirstOrDefault(c => c.Name == SelectedMetricName);
            if (config != null)
                return (config.MinValue, config.MaxValue);
            return (0, 100);
        }

        /// <summary>
        /// 设备实时指标值更新处理：仅关注当前选中指标，追加实时点并刷新头部实时值。
        /// </summary>
        /// <param name="metricName">更新值的指标名称。</param>
        /// <param name="value">最新指标值。</param>
        /// <remarks>事件由定时器所在的后台线程触发，非 UI 线程时通过 Dispatcher 封送回 UI 线程执行。</remarks>
        // 任意指标值更新（定时器线程触发，需切回 UI 线程）
        private void OnMetricValueChanged(string metricName, double value)
        {
            if (metricName != _selectedMetricName) return;

            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(() => OnMetricValueChanged(metricName, value));
                return;
            }

            AddRealtimeDataPoint(value);
            UpdateCurrentValueText();
        }

        /// <summary>
        /// 向曲线追加一个当前时刻的实时点，仅保留最近 60 个点，并同步收紧 X 轴显示范围。
        /// </summary>
        /// <param name="value">最新指标值。</param>
        private void AddRealtimeDataPoint(double value)
        {
            var now = DateTime.Now;
            _chartPoints.Add(new ObservablePoint(now.ToOADate(), value));

            // 仅保留最近 60 个实时点（历史数据已由服务层统一写入数据库）
            if (_chartPoints.Count > 60)
            {
                _chartPoints.RemoveAt(0);
            }

            if (_chartPoints.Count > 0)
            {
                XAxes[0].MinLimit = _chartPoints[0].X;
                XAxes[0].MaxLimit = _chartPoints[_chartPoints.Count - 1].X;
            }
        }

        /// <summary>
        /// 刷新头部实时值文本：无选中指标或值为 NaN 时显示“--”，否则保留一位小数。
        /// </summary>
        private void UpdateCurrentValueText()
        {
            if (string.IsNullOrEmpty(_selectedMetricName))
            {
                CurrentValueText = "--";
                return;
            }
            double v = _device.GetMetricValue(_selectedMetricName);
            CurrentValueText = double.IsNaN(v) ? "--" : v.ToString("F1");
        }

        /// <summary>
        /// 获取当前选中指标的单位文本（供 XAML 中 Y 轴标签格式化回调使用）；无配置时为空字符串。
        /// </summary>
        /// <returns>当前指标单位。</returns>
        public string GetMetricUnit()
        {
            var config = _metricConfigs.FirstOrDefault(c => c.Name == SelectedMetricName);
            return config?.Unit ?? "";
        }

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件；属性名缺省时由调用方成员名自动填充。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称，null 时由 <see cref="CallerMemberNameAttribute"/> 自动传入。</param>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 释放本视图模型：退订设备的实时指标值变更事件。
        /// </summary>
        public void Dispose()
        {
            _device.MetricValueChanged -= OnMetricValueChanged;
        }
    }
}
