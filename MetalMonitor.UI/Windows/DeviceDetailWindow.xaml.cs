using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI.Windows
{
    public partial class DeviceDetailWindow : Window
    {
        private DeviceDetailViewModel? _viewModel;

        public DeviceDetailWindow(DeviceDetailViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        // 窗口激活时刷新指标配置（从系统设置改完阈值/最值后切回即生效）
        private async void OnActivated(object sender, EventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.RefreshConfigsAsync();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel?.Dispose();
            base.OnClosed(e);
            // 非模态窗口关闭后显式激活主窗口，避免焦点跳到 VS 或其他后台窗口
            if (Owner != null)
            {
                Owner.Activate();
                Owner.Topmost = true;
                Owner.Topmost = false;
                Owner.Focus();
            }
        }

        private async void OnQueryButtonClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.LoadHistoricalDataAsync();
            }
        }
    }
}