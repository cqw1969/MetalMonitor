using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;

//using System.Diagnostics;

namespace MetalMonitor.UI.ViewModels
{
    public class MainViewModel : IDisposable
    {
        //public ObservableCollection<Device> Devices { get; set; }
        public ObservableCollection<DeviceCardViewModel> DeviceCardViewModels { get; set; }

        private readonly IDataService _dataService;
        private readonly System.Timers.Timer _dataTimer;
        private readonly IAlarmService _alarmService;

        // 报警记录
        public ObservableCollection<string> AlarmLogs { get; set; } = new();

        public MainViewModel()
        {
            //创建报警服务实例
            _alarmService = new AlarmService();

            _alarmService.AlarmStateChanged += OnAlarmStateChanged;

            // 使用模拟数据服务（暑假换成 HardwareDataService）
            _dataService = new SimulatedDataService(_alarmService);

            //Devices = new ObservableCollection<Device>();
            DeviceCardViewModels = new ObservableCollection<DeviceCardViewModel>();

            _dataTimer = new System.Timers.Timer(1000);
            _dataTimer.Elapsed += OnTimerElapsed;
            _dataTimer.AutoReset = true;
            _dataTimer.Start();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                //Debug.WriteLine($"当前是否UI线程：{Application.Current.Dispatcher.CheckAccess()}");
                // 从数据服务获取最新设备数据
                var devicesFromService = _dataService.GetAllDevices();

                // 将对 ObservableCollection 的修改调度到 UI 线程
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    //Debug.WriteLine($"当前是否UI线程：{Application.Current.Dispatcher.CheckAccess()}");
                    // 首次加载时添加设备
                    if (DeviceCardViewModels.Count == 0)
                    {
                        foreach (var device in devicesFromService)
                        {
                            DeviceCardViewModels.Add(new DeviceCardViewModel(device));
                        }
                    }
                    // 之后定时器触发时，Device 属性已被 SimulatedDataService 更新
                    // DeviceCardViewModel 在构造时已订阅 Device.PropertyChanged，
                    // 所以温度变化会自动触发 DeviceCardViewModel 内部的 AddTemperaturePoint，
                    // UI 曲线会自动刷新，无需我们在这里做任何额外操作。
                });
            }
            catch (Exception ex)
            {
                // 记录异常信息到报警日志
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