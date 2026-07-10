using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;

//using System.Diagnostics;

namespace MetalMonitor.UI.ViewModels
{
    public class MainViewModel : IDisposable
    {
        public ObservableCollection<DeviceCardViewModel> DeviceCardViewModels { get; set; }

        private readonly IDeviceService _deviceService;
        private readonly System.Timers.Timer _dataTimer;
        private readonly IAlarmService _alarmService;

        public ObservableCollection<string> AlarmLogs { get; set; } = new();

        public string DataSource => _deviceService.DataSource;

        public MainViewModel(IDeviceService deviceService, IAlarmService alarmService)
        {
            _deviceService = deviceService;
            _alarmService = alarmService;

            _alarmService.AlarmStateChanged += OnAlarmStateChanged;

            //Devices = new ObservableCollection<Device>();
            DeviceCardViewModels = new ObservableCollection<DeviceCardViewModel>();

            _dataTimer = new System.Timers.Timer(1000);
            _dataTimer.Elapsed += OnTimerElapsed;
            _dataTimer.AutoReset = true;
            _dataTimer.Start();
        }

        private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                await _deviceService.UpdateDevicesAsync();

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (DeviceCardViewModels.Count == 0)
                    {
                        var devices = _deviceService.GetDevices();
                        foreach (var device in devices)
                        {
                            DeviceCardViewModels.Add(new DeviceCardViewModel(device, _deviceService));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✗ 数据更新异常：{ex.Message}");
                });
            }
        }

        private void OnAlarmStateChanged(string deviceName, bool isAlarming, double temperature)
        {
            // 调度到 UI 线程添加日志
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                string log = isAlarming
                    ? $"[{DateTime.Now:HH:mm:ss}] ⚠ {deviceName} 报警！温度：{temperature:F1}℃"
                    : $"[{DateTime.Now:HH:mm:ss}] ✓ {deviceName} 恢复正常，温度：{temperature:F1}℃";
                AlarmLogs.Add(log);
            });
        }

        #region IDisposable

        private bool _disposed = false;

        // 实现 IDisposable 接口，用于释放 Timer 和取消事件订阅
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
                // 停止并释放 Timer
                _dataTimer?.Stop();
                _dataTimer?.Dispose();

                // 取消报警服务事件订阅
                if (_alarmService != null)
                {
                    _alarmService.AlarmStateChanged -= OnAlarmStateChanged;
                }

                // 释放所有 DeviceCardViewModel
                foreach (var vm in DeviceCardViewModels)
                {
                    vm.Dispose();
                }
            }

            _disposed = true;
        }

        ~MainViewModel()
        {
            Dispose(false);
        }

        #endregion
    }
}