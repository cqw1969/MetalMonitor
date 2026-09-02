using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using MetalMonitor.UI.Views;
using MetalMonitor.UI.Windows;
using System.Windows;

namespace MetalMonitor.UI
{
    public partial class MainWindow : Window
    {
        private MainViewModel? _viewModel;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel?.Dispose();
            base.OnClosed(e);
        }

        private void OnDeviceCardClicked(object sender, DeviceCardClickedEventArgs e)
        {
            if (_viewModel != null)
            {
                var detailViewModel = new DeviceDetailViewModel(e.Device, _viewModel.GetDatabaseService());
                var detailWindow = new DeviceDetailWindow(detailViewModel);
                detailWindow.Owner = this;
                detailWindow.Show();
            }
        }

        private void OnSelectDatabasePath(object sender, RoutedEventArgs e)
        {
            _viewModel?.SelectDatabasePath();
        }

        private void OnOpenDeviceManagement(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                var mgmtViewModel = new DeviceManagementViewModel(
                    _viewModel.GetDatabaseService(),
                    _viewModel.GetDeviceService());
                var mgmtWindow = new DeviceManagementWindow(mgmtViewModel);
                mgmtWindow.Owner = this;
                mgmtWindow.Show();
            }
        }

        private void OnOpenSystemSettings(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                var settingsViewModel = new SystemSettingsViewModel(
                    _viewModel.GetDatabaseService(),
                    _viewModel.GetDeviceService());
                var settingsWindow = new SystemSettingsWindow(settingsViewModel);
                settingsWindow.Owner = this;
                settingsWindow.Show();
            }
        }
    }
}