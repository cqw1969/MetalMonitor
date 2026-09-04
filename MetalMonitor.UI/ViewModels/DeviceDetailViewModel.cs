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
    public class DeviceDetailViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly Device _device;
        private readonly IDatabaseService _databaseService;
        private DateTime _startDate = DateTime.Today;
        private DateTime _endDate = DateTime.Now;
        private string _selectedMetricName = "Temperature";
        private bool _isLoading = false;
        private List<MetricConfig> _metricConfigs = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public Device Device => _device;

        public DateTime StartDate
        {
            get => _startDate;
            set { _startDate = value; OnPropertyChanged(); }
        }

        public DateTime EndDate
        {
            get => _endDate;
            set { _endDate = value; OnPropertyChanged(); }
        }

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

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public ObservableCollection<DataRecord> HistoricalRecords { get; } = new();
        public ObservableCollection<MetricConfig> AvailableMetrics { get; } = new();

        public ISeries[] Series { get; set; }
        public Axis[] XAxes { get; set; }
        public Axis[] YAxes { get; set; }

        private ObservableCollection<ObservablePoint> _chartPoints = new();

        // 头部实时值显示
        private string _currentValueText = "--";
        public string CurrentValueText
        {
            get => _currentValueText;
            private set { _currentValueText = value; OnPropertyChanged(); }
        }

        public string MetricDisplayName
        {
            get
            {
                var config = _metricConfigs.FirstOrDefault(c => c.Name == _selectedMetricName);
                return config?.DisplayName ?? _selectedMetricName;
            }
        }

        public string CurrentMetricUnit
        {
            get
            {
                var config = _metricConfigs.FirstOrDefault(c => c.Name == _selectedMetricName);
                return config?.Unit ?? "";
            }
        }

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

        private (double min, double max) GetMetricMinMax()
        {
            var config = _metricConfigs.FirstOrDefault(c => c.Name == SelectedMetricName);
            if (config != null)
                return (config.MinValue, config.MaxValue);
            return (0, 100);
        }

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

        public string GetMetricUnit()
        {
            var config = _metricConfigs.FirstOrDefault(c => c.Name == SelectedMetricName);
            return config?.Unit ?? "";
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            _device.MetricValueChanged -= OnMetricValueChanged;
        }
    }
}
