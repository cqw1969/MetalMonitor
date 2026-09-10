using MetalMonitor.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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

        // 点击 DataGrid 空白区域（行与滚动条之外）时取消选中，
        // 取消后 SelectedMetricConfig 变为 null，修改/删除按钮随之禁用、编辑栏清空
        private void OnDataGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid) return;
            if (e.OriginalSource is not DependencyObject source) return;

            // 命中点向上找不到行、也不在滚动条上 → 视为点击空白区域
            if (FindParent<DataGridRow>(source) == null
                && FindParent<System.Windows.Controls.Primitives.ScrollBar>(source) == null)
            {
                grid.UnselectAll();
            }
        }

        // 在可视化树中向上查找指定类型的父元素
        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is T matched) return matched;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
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
