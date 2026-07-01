using MetalMonitor.Core;
using System;
using System.Timers;

namespace MetalMonitor.UI.ViewModels
{
    public class MainViewModel
    {
        // 当前要展示的设备，先做一台
        public Device CurrentDevice { get; set; }

        private readonly System.Timers.Timer _dataTimer;
        private readonly Random _random = new();

        public MainViewModel()
        {
            // 初始化一台设备，名字就叫 "1号冲压机"
            CurrentDevice = new Device("1号冲压机", 65);

            // 设置定时器，每秒更新一次温度
            _dataTimer = new System.Timers.Timer(1000);
            _dataTimer.Elapsed += OnTimerElapsed;
            _dataTimer.AutoReset = true;
            _dataTimer.Start();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 模拟温度在 55~90 之间波动
            double newTemp = 55 + _random.NextDouble() * 35;
            CurrentDevice.Temperature = Math.Round(newTemp, 1);
        }
    }
}