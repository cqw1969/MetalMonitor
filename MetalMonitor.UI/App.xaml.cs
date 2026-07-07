using Microsoft.Extensions.DependencyInjection;
using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAlarmService, AlarmService>();
            services.AddSingleton<IDeviceService, DeviceService>();
            services.AddSingleton<MainViewModel>();

            _serviceProvider = services.BuildServiceProvider();

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