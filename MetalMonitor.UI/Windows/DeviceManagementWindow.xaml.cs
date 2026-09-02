using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI.Windows
{
    public partial class DeviceManagementWindow : Window
    {
        private readonly DeviceManagementViewModel _viewModel;

        public DeviceManagementWindow(DeviceManagementViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
            Loaded += async (s, e) => await _viewModel.LoadDevicesAsync();
        }

        private async void OnAddDevice(object sender, RoutedEventArgs e)
        {
            await _viewModel.AddDeviceAsync();
        }

        private async void OnUpdateDevice(object sender, RoutedEventArgs e)
        {
            await _viewModel.UpdateDeviceAsync();
        }

        private async void OnDeleteDevice(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要删除该设备吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await _viewModel.DeleteDeviceAsync();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // 窗口关闭后刷新主界面的设备列表
            _viewModel.ReloadMainDeviceListAsync().ConfigureAwait(false);
            base.OnClosed(e);
        }
    }
}
