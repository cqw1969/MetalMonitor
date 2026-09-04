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

        public App()
        {
            // 全局异常处理：防止未处理异常导致无报错闪退，记录到 error_fix_log.md
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        // UI 线程未处理异常：记录日志并阻止闪退
        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogError("UI线程未处理异常", e.Exception);
            MessageBox.Show($"发生异常：{e.Exception.Message}\n\n详情已记录到 error_fix_log.md", "错误",
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

        // 记录错误到 error_fix_log.md（开发阶段放解决方案同级目录，部署阶段回退到程序运行目录）
        private static void LogError(string source, Exception ex)
        {
            try
            {
                // 从输出目录向上查找解决方案目录
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var slnDir = dir;
                while (!string.IsNullOrEmpty(slnDir) && !File.Exists(Path.Combine(slnDir, "MetalMonitor.sln")))
                    slnDir = Path.GetDirectoryName(slnDir);
                if (string.IsNullOrEmpty(slnDir)) slnDir = dir;

                var path = Path.Combine(slnDir, "error_fix_log.md");
                var sb = new StringBuilder();
                sb.AppendLine($"## [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name} ({source})");
                sb.AppendLine($"- 消息: {ex.Message}");
                sb.AppendLine($"- 堆栈跟踪:");
                sb.AppendLine("```");
                sb.AppendLine(ex.StackTrace ?? "(无堆栈信息)");
                sb.AppendLine("```");
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                // 记录日志失败时不影响程序运行
            }
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