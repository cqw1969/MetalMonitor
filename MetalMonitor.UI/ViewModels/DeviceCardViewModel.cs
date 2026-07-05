using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using MetalMonitor.Core;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MetalMonitor.UI.ViewModels
{
    public class DeviceCardViewModel : IDisposable
    {
        public Device Device { get; }

        // 温度历史记录，用于曲线
        public ObservableCollection<ObservablePoint> TemperaturePoints { get; } = new();

        // 曲线系列定义
        public ISeries[] Series { get; set; }

        // X 轴配置
        public Axis[] XAxes { get; set; }

        // Y 轴配置
        public Axis[] YAxes { get; set; }

        private const int MaxHistoryPoints = 60; // 保留最近60个点

        public DeviceCardViewModel(Device device)
        {
            Device = device;

            // 订阅属性变化
            Device.PropertyChanged += OnDevicePropertyChanged;

            // 初始化曲线
            Series = new ISeries[]
            {
                new LineSeries<ObservablePoint>
                {
                    Values = TemperaturePoints,
                    Fill = null,
                    Stroke = new LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint(SKColors.LightGreen) { StrokeThickness = 2 },
                    GeometrySize = 0, // 不显示数据点圆圈
                    LineSmoothness = 0.3 // 轻微平滑
                }
            };

            XAxes = new Axis[]
            {
                new Axis
                {
                    ShowSeparatorLines = false,
                    IsVisible = false // 隐藏X轴
                }
            };

            YAxes = new Axis[]
            {
                new Axis
                {
                    MinLimit = 40,
                    MaxLimit = 100,
                    ShowSeparatorLines = false,
                    IsVisible = false // 隐藏Y轴，迷你图不需要坐标轴
                }
            };
        }

        // 添加新温度点到历史记录
        public void AddTemperaturePoint(double temperature)
        {
            TemperaturePoints.Add(new ObservablePoint(TemperaturePoints.Count, temperature));

            // 保持点数不超标
            while (TemperaturePoints.Count > MaxHistoryPoints)
            {
                TemperaturePoints.RemoveAt(0);
                // 重新索引 X 坐标，保持连续
                for (int i = 0; i < TemperaturePoints.Count; i++)
                {
                    TemperaturePoints[i].X = i;
                }
            }
        }

        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Device.Temperature))
            {
                AddTemperaturePoint(Device.Temperature);
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