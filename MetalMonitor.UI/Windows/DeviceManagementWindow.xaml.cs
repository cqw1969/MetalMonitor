using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
            Loaded += async (s, e) =>
            {
                try { await _viewModel.LoadDevicesAsync(); }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载设备列表失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
        }

        private async void OnAddDevice(object sender, RoutedEventArgs e)
        {
            try { await _viewModel.AddDeviceAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show($"添加设备失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void OnUpdateDevice(object sender, RoutedEventArgs e)
        {
            try { await _viewModel.UpdateDeviceAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show($"修改设备失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void OnDeleteDevice(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要删除该设备吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                try { await _viewModel.DeleteDeviceAsync(); }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除设备失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        // 点击 DataGrid 空白区域（行与滚动条之外）时取消选中，
        // 取消后 SelectedDevice 变为 null，修改/删除按钮随之禁用、编辑栏清空
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
            base.OnClosed(e);
            // 非模态窗口关闭后显式激活主窗口，避免焦点跳到 VS 或其他后台窗口
            if (Owner != null)
            {
                Owner.Activate();
                // Topmost 技巧确保窗口被提到前台并获取焦点
                Owner.Topmost = true;
                Owner.Topmost = false;
                Owner.Focus();
            }
        }
    }
}
