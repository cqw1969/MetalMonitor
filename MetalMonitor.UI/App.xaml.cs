using Microsoft.Extensions.DependencyInjection;
using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MetalMonitor.core.Services;

namespace MetalMonitor.UI
{
    /// <summary>
    /// 应用程序入口类：构建依赖注入容器、注册 Core 层各服务与 ViewModel，
    /// 挂载全局未处理异常兜底处理，并在启动时初始化数据库与 MQTT 后显示主窗口。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>依赖注入服务提供者（容器），持有 Core 层各服务及 ViewModel 的单例实例，应用退出时释放。</summary>
        private ServiceProvider? _serviceProvider;

        /// <summary>全局滚动文件日志记录器实例，供三类未处理异常回调统一写入日志。</summary>
        // 运行时异常统一写入 logs/ 目录的滚动日志：单文件 5MB，超 10 个删除最早 5 个
        private static readonly RollingFileLogger _logger = new();

        /// <summary>
        /// 初始化 <see cref="App"/> 的新实例，并订阅三类全局未处理异常事件。
        /// </summary>
        /// <remarks>
        /// 此处仅做兜底日志记录，分别订阅 <see cref="Application.DispatcherUnhandledException"/>、
        /// <see cref="AppDomain.UnhandledException"/> 与 <see cref="TaskScheduler.UnobservedTaskException"/>，
        /// 防止未处理异常导致无报错闪退。
        /// </remarks>
        public App()
        {
            // 全局异常处理：防止未处理异常导致无报错闪退，记录到 logs/ 滚动日志文件
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>
        /// UI 线程未处理异常回调：写入滚动日志、弹窗提示用户，并将异常标记为已处理以阻止应用闪退。
        /// </summary>
        /// <param name="sender">事件发送者（WPF 调度器）。</param>
        /// <param name="e">未处理异常事件参数，<see cref="System.Windows.Threading.DispatcherUnhandledExceptionEventArgs.Handled"/> 置为 true 表示异常已处理。</param>
        // UI 线程未处理异常：记录日志并阻止闪退
        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogError("UI线程未处理异常", e.Exception);
            MessageBox.Show($"发生异常：{e.Exception.Message}\n\n详情已记录到 logs 目录下的日志文件", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        /// <summary>
        /// 非 UI 线程（后台线程）未处理异常回调：仅将异常写入滚动日志。
        /// </summary>
        /// <param name="sender">事件发送者（当前 <see cref="AppDomain"/>）。</param>
        /// <param name="e">未处理异常事件参数，<see cref="UnhandledExceptionEventArgs.ExceptionObject"/> 为抛出的异常对象。</param>
        /// <remarks>此回调中无法阻止进程终止，仅做兜底日志记录。</remarks>
        // 非 UI 线程未处理异常：记录日志（无法阻止进程终止）
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogError("非UI线程未处理异常", ex);
        }

        /// <summary>
        /// 未被观察的 Task 异常回调：写入滚动日志并调用 <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/> 标记为已观察，阻止进程闪退。
        /// </summary>
        /// <param name="sender">事件发送者（<see cref="TaskScheduler"/>），可能为 null。</param>
        /// <param name="e">未观察任务异常事件参数，包含聚合异常 <see cref="UnobservedTaskExceptionEventArgs.Exception"/>。</param>
        // 未观察的 Task 异常：记录并标记已观察，阻止闪退
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogError("未观察的Task异常", e.Exception);
            e.SetObserved();
        }

        /// <summary>
        /// 将运行时异常按指定来源写入 logs/ 目录下的滚动日志文件。
        /// </summary>
        /// <param name="source">异常来源说明（如“UI线程未处理异常”）。</param>
        /// <param name="ex">待记录的异常对象。</param>
        // 运行时异常写入 logs/ 滚动日志文件（5MB 滚动、按日期时间命名、超限自动清理）
        private static void LogError(string source, Exception ex)
        {
            _logger.Log(source, ex);
        }

        /// <summary>
        /// 应用启动重写：构建 DI 容器并注册 Core 层服务（报警、MQTT、数据库、设备服务）与 <see cref="MainViewModel"/>，
        /// 依次初始化数据库和 MQTT 连接，最后创建并显示主窗口，设置主窗口关闭即退出应用。
        /// </summary>
        /// <param name="e">启动事件参数（命令行参数等）。</param>
        /// <remarks>声明为 async void 以匹配 WPF 生命周期重写签名；await 期间的异常由全局异常处理兜底。</remarks>
        protected override async void OnStartup(StartupEventArgs e)
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAlarmService, AlarmService>();
            services.AddSingleton<IMqttService, MqttService>();
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<ISerialService, SerialService>();
            services.AddSingleton<FrameParser>();
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

        /// <summary>
        /// 应用退出重写：释放依赖注入容器（连带释放容器内各服务单例），再调用基类退出逻辑。
        /// </summary>
        /// <param name="e">退出事件参数。</param>
        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}