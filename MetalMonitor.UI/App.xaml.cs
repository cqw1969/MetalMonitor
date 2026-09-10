using Microsoft.Extensions.DependencyInjection;
using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace MetalMonitor.UI
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        // 运行时异常统一写入 logs/ 目录的滚动日志：单文件 5MB，超 10 个删除最早 5 个
        private static readonly RollingFileLogger _logger = new();

        public App()
        {
            // 全局异常处理：防止未处理异常导致无报错闪退，记录到 logs/ 滚动日志文件
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        // UI 线程未处理异常：记录日志并阻止闪退
        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogError("UI线程未处理异常", e.Exception);
            MessageBox.Show($"发生异常：{e.Exception.Message}\n\n详情已记录到 logs 目录下的日志文件", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        // 非 UI 线程未处理异常：记录日志（无法阻止进程终止）
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogError("非UI线程未处理异常", ex);
        }

        // 未观察的 Task 异常：记录并标记已观察，阻止闪退
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogError("未观察的Task异常", e.Exception);
            e.SetObserved();
        }

        // 运行时异常写入 logs/ 滚动日志文件（5MB 滚动、按日期时间命名、超限自动清理）
        private static void LogError(string source, Exception ex)
        {
            _logger.Log(source, ex);
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAlarmService, AlarmService>();
            services.AddSingleton<IMqttService, MqttService>();
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<IDeviceService, DeviceService>();
            services.AddSingleton<MainViewModel>();

            _serviceProvider = services.BuildServiceProvider();

            var databaseService = _serviceProvider.GetRequiredService<IDatabaseService>();
            await databaseService.InitializeAsync();

            var deviceService = _serviceProvider.GetRequiredService<IDeviceService>() as DeviceService;
            if (deviceService != null)
            {
                await deviceService.InitializeMqttAsync();
            }

            var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(viewModel);
            mainWindow.Show();

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}