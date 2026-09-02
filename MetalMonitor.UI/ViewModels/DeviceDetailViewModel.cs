using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MetalMonitor.Core;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MetalMonitor.UI.ViewModels
{
    public class DeviceDetailViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int BATCH_SIZE = 50;

        private readonly Device _device;
        private readonly IDatabaseService _databaseService;
        private DateTime _startDate = DateTime.Today;
        private DateTime _endDate = DateTime.Now;
        private string _selectedMetricName = "Temperature";
        private bool _isLoading = false;
        private List<MetricConfig> _metricConfigs = new();

        private readonly ConcurrentQueue<DataRecord> _pendingRecords = new();

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
                FlushPendingRecords().ConfigureAwait(false);
                _selectedMetricName = value;
                OnPropertyChanged();
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

        public DeviceDetailViewModel(Device device, IDatabaseService databaseService)
        {
            _device = device;
            _databaseService = databaseService;

            _device.PropertyChanged += OnDevicePropertyChanged;

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
                        return timestamp.ToString("HH:mm:ss");
                    },
                    MinLimit = 0,
                    MaxLimit = 100
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
            if (AvailableMetrics.Count > 0)
                _selectedMetricName = AvailableMetrics[0].Name;

            await LoadHistoricalDataAsync();
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

        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Device.Temperature) && SelectedMetricName == "Temperature")
            {
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.BeginInvoke(() => AddRealtimeDataPoint(Device.Temperature));
                }
                else
                {
                    AddRealtimeDataPoint(Device.Temperature);
                }
            }
        }

        private void AddRealtimeDataPoint(double value)
        {
            var now = DateTime.Now;
            _chartPoints.Add(new ObservablePoint(now.ToOADate(), value));

            if (_chartPoints.Count > 60)
            {
                _chartPoints.RemoveAt(0);
            }

            if (_chartPoints.Count > 0)
            {
                XAxes[0].MinLimit = _chartPoints[0].X;
                XAxes[0].MaxLimit = _chartPoints[_chartPoints.Count - 1].X;
            }

            AddDataPoint(value);
        }

        public void AddDataPoint(double value)
        {
            var record = new DataRecord
            {
                DeviceId = _device.Id,
                DeviceName = _device.Name,
                MetricName = SelectedMetricName,
                Value = value,
                Unit = GetMetricUnit(),
                Timestamp = DateTime.Now
            };

            _pendingRecords.Enqueue(record);

            if (_pendingRecords.Count >= BATCH_SIZE)
            {
                FlushPendingRecords().ConfigureAwait(false);
            }
        }

        private async Task FlushPendingRecords()
        {
            var batch = new List<DataRecord>();
            while (_pendingRecords.TryDequeue(out var record))
            {
                batch.Add(record);
            }

            if (batch.Count > 0)
            {
                await _databaseService.AddDataRecordsBatchAsync(batch);
            }
        }

        public string GetMetricDisplayName()
        {
            var config = _metricConfigs.FirstOrDefault(c => c.Name == SelectedMetricName);
            return config?.DisplayName ?? SelectedMetricName;
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

        public async void Dispose()
        {
            _device.PropertyChanged -= OnDevicePropertyChanged;
            await FlushPendingRecords();
        }
    }
}
