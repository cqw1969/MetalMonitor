using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MetalMonitor.Core;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace MetalMonitor.UI.ViewModels
{
    public class DeviceCardViewModel : IDisposable
    {
        public Device Device { get; }
        private readonly IDeviceService _deviceService;

        public ObservableCollection<ObservablePoint> TemperaturePoints { get; } = new();
        public ObservableCollection<ObservablePoint> AlarmPoints { get; } = new();

        public ISeries[] Series { get; set; }

        public Axis[] XAxes { get; set; }

        public Axis[] YAxes { get; set; }

        public DeviceCardViewModel(Device device, IDeviceService deviceService)
        {
            Device = device;
            _deviceService = deviceService;

            Device.PropertyChanged += OnDevicePropertyChanged;

            Series = new ISeries[]
            {
                new LineSeries<ObservablePoint>
                {
                    Values = TemperaturePoints,
                    Fill = new SolidColorPaint(new SKColor(108, 212, 108, 80)),
                    Stroke = new SolidColorPaint(SKColors.LightGreen) { StrokeThickness = 2 },
                    GeometrySize = 0,
                    LineSmoothness = 0.4
                },
                new ScatterSeries<ObservablePoint>
                {
                    Values = AlarmPoints,
                    GeometrySize = 5,
                    Fill = new SolidColorPaint(SKColors.Red),
                    Stroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 1 }
                }
            };

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

            ApplyMetricLimits();
        }

        // 根据指标配置更新 Y 轴范围（与指标设置中的最值一致）
        public void ApplyMetricLimits()
        {
            var tempConfig = _deviceService.GetMetricConfigs().FirstOrDefault(c => c.Name == "Temperature");
            if (tempConfig != null)
            {
                YAxes[0].MinLimit = tempConfig.MinValue;
                YAxes[0].MaxLimit = tempConfig.MaxValue;
            }
        }

        public void AddTemperaturePoint(double temperature)
        {
            TemperaturePoints.Add(new ObservablePoint(TemperaturePoints.Count, temperature));

            if (_deviceService.ShouldAlarm("Temperature", temperature))
            {
                AlarmPoints.Add(new ObservablePoint(TemperaturePoints.Count - 1, temperature));
            }

            while (TemperaturePoints.Count > _deviceService.MaxHistoryPoints)
            {
                TemperaturePoints.RemoveAt(0);
                for (int i = 0; i < TemperaturePoints.Count; i++)
                {
                    TemperaturePoints[i].X = i;
                }
                for (int i = AlarmPoints.Count - 1; i >= 0; i--)
                {
                    AlarmPoints[i].X -= 1;
                    if (AlarmPoints[i].X < 0)
                    {
                        AlarmPoints.RemoveAt(i);
                    }
                }
            }
        }

        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Device.Temperature))
            {
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.BeginInvoke(() => AddTemperaturePoint(Device.Temperature));
                }
                else
                {
                    AddTemperaturePoint(Device.Temperature);
                }
            }
        }

        #region IDisposable

        private bool _disposed = false;

        // 实现 IDisposable 接口，用于释放资源和取消事件订阅
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
                // 取消订阅事件，防止内存泄漏
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