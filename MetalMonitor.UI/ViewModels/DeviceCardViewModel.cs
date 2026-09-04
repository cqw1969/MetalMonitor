using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MetalMonitor.Core;
using SkiaSharp;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MetalMonitor.UI.ViewModels
{
    public class DeviceCardViewModel : INotifyPropertyChanged, IDisposable
    {
        public Device Device { get; }
        private readonly IDeviceService _deviceService;

        private string _metricName = "Temperature";

        // 每个指标独立的历史点/报警点集合
        private readonly Dictionary<string, ObservableCollection<ObservablePoint>> _pointsByMetric = new();
        private readonly Dictionary<string, ObservableCollection<ObservablePoint>> _alarmPointsByMetric = new();

        private LineSeries<ObservablePoint> _lineSeries;
        private ScatterSeries<ObservablePoint> _alarmSeries;

        public ISeries[] Series { get; private set; }
        public Axis[] XAxes { get; set; }
        public Axis[] YAxes { get; set; }

        public DeviceCardViewModel(Device device, IDeviceService deviceService)
        {
            Device = device;
            _deviceService = deviceService;

            Device.MetricValueChanged += OnMetricValueChanged;
            Device.PropertyChanged += OnDevicePropertyChanged;

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

        private LineSeries<ObservablePoint> CreateLineSeries() => new()
        {
            Fill = new SolidColorPaint(new SKColor(108, 212, 108, 80)),
            Stroke = new SolidColorPaint(SKColors.LightGreen) { StrokeThickness = 2 },
            GeometrySize = 0,
            LineSmoothness = 0.4
        };

        private ScatterSeries<ObservablePoint> CreateAlarmSeries() => new()
        {
            GeometrySize = 5,
            Fill = new SolidColorPaint(SKColors.Red),
            Stroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 1 }
        };

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

        private MetricConfig? CurrentConfig =>
            _deviceService.GetMetricConfigs().FirstOrDefault(c => c.Name == _metricName);

        public string MetricDisplayName => CurrentConfig?.DisplayName ?? _metricName;

        public string CurrentUnit => CurrentConfig?.Unit ?? string.Empty;

        // 指标是否启用（停用后卡片置灰）
        public bool IsMetricActive => CurrentConfig?.IsActive ?? false;

        // 当前指标是否处于报警状态
        public bool IsMetricAlarming => Device.IsMetricAlarming(_metricName);

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
            OnPropertyChanged(nameof(ValueText));
        }

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

        private ObservableCollection<ObservablePoint> GetPoints(string metric)
        {
            if (!_pointsByMetric.TryGetValue(metric, out var col))
            {
                col = new ObservableCollection<ObservablePoint>();
                _pointsByMetric[metric] = col;
            }
            return col;
        }

        private ObservableCollection<ObservablePoint> GetAlarmPoints(string metric)
        {
            if (!_alarmPointsByMetric.TryGetValue(metric, out var col))
            {
                col = new ObservableCollection<ObservablePoint>();
                _alarmPointsByMetric[metric] = col;
            }
            return col;
        }

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

        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Device.IsAlarming))
            {
                Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsMetricAlarming)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region IDisposable

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Device.MetricValueChanged -= OnMetricValueChanged;
                Device.PropertyChanged -= OnDevicePropertyChanged;
            }

            _disposed = true;
        }

        ~DeviceCardViewModel()
        {
            Dispose(false);
        }

        #endregion
    }
}
