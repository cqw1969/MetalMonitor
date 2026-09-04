using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI.Windows
{
    public partial class SystemSettingsWindow : Window
    {
        private readonly SystemSettingsViewModel _viewModel;

        public SystemSettingsWindow(SystemSettingsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
            Loaded += async (s, e) => await _viewModel.LoadMetricConfigsAsync();
        }

        private async void OnAddMetric(object sender, RoutedEventArgs e)
        {
            await _viewModel.AddMetricConfigAsync();
        }

        private async void OnUpdateMetric(object sender, RoutedEventArgs e)
        {
            await _viewModel.UpdateMetricConfigAsync();
        }

        private async void OnDeleteMetric(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要删除该指标配置吗？已采集的历史数据将保留但不再产生该指标的新数据。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await _viewModel.DeleteMetricConfigAsync();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // 指标增删改操作已在各自处理中即时同步设备服务配置，
            // 此处不再做 fire-and-forget 异步刷新（曾导致关闭窗口时无报错闪退）
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
    }
}
