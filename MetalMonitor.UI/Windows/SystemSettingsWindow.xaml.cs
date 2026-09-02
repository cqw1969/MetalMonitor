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
            // 窗口关闭后刷新设备服务的指标配置和报警阈值
            _viewModel.ReloadServiceConfigsAsync().ConfigureAwait(false);
            base.OnClosed(e);
        }
    }
}
