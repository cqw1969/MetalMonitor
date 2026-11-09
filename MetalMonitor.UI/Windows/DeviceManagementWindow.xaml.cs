using MetalMonitor.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetalMonitor.UI.Windows
{
    /// <summary>
    /// 设备管理窗口 code-behind：窗口加载时拉取设备列表，转发增/改/删按钮事件并做异常弹窗与删除确认，
    /// 处理 DataGrid 空白区域点击取消选中，并在非模态关闭后重新激活主窗口。
    /// </summary>
    public partial class DeviceManagementWindow : Window
    {
        /// <summary>本窗口的设备管理视图模型。</summary>
        private readonly DeviceManagementViewModel _viewModel;

        /// <summary>
        /// 初始化 <see cref="DeviceManagementWindow"/> 的新实例，设置 DataContext，
        /// 并在窗口 Loaded 时异步加载设备列表（加载失败弹窗提示）。
        /// </summary>
        /// <param name="viewModel">设备管理视图模型。</param>
        /// <remarks>Loaded 回调为 async void 事件订阅；加载异常在回调内捕获并以消息框提示。</remarks>
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

        /// <summary>
        /// “添加设备”按钮点击处理：异步执行新增，异常时弹窗提示。
        /// </summary>
        /// <param name="sender">事件发送者（添加按钮）。</param>
        /// <param name="e">路由事件参数。</param>
        private async void OnAddDevice(object sender, RoutedEventArgs e)
        {
            try { await _viewModel.AddDeviceAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show($"添加设备失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// “修改设备”按钮点击处理：异步执行修改，异常时弹窗提示。
        /// </summary>
        /// <param name="sender">事件发送者（修改按钮）。</param>
        /// <param name="e">路由事件参数。</param>
        private async void OnUpdateDevice(object sender, RoutedEventArgs e)
        {
            try { await _viewModel.UpdateDeviceAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show($"修改设备失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// “删除设备”按钮点击处理：先弹出确认框，用户选择“是”后异步执行删除，异常时弹窗提示。
        /// </summary>
        /// <param name="sender">事件发送者（删除按钮）。</param>
        /// <param name="e">路由事件参数。</param>
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

        /// <summary>
        /// DataGrid 预览鼠标左键按下处理：点击行与滚动条之外的空白区域时取消全部选中，
        /// 使 <see cref="DeviceManagementViewModel.SelectedDevice"/> 变为 null，修改/删除按钮随之禁用、编辑栏清空。
        /// </summary>
        /// <param name="sender">事件发送者（DataGrid）。</param>
        /// <param name="e">鼠标按钮事件参数，<see cref="RoutedEventArgs.OriginalSource"/> 为命中的可视化元素。</param>
        /// <remarks>使用 Preview 隧道事件以便在元素自身处理鼠标事件之前完成命中判定。</remarks>
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

        /// <summary>
        /// 在可视化树中从指定元素向上查找指定类型的最近祖先。
        /// </summary>
        /// <typeparam name="T">要查找的祖先元素类型。</typeparam>
        /// <param name="child">查找起点元素（命中测试的原始元素）。</param>
        /// <returns>找到的祖先元素；不存在时返回 null。</returns>
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

        /// <summary>
        /// 窗口关闭重写：在非模态关闭后显式激活主窗口（借助 Topmost 置位复位把主窗口提到前台并获取焦点），避免焦点跳到其他后台窗口。
        /// </summary>
        /// <param name="e">窗口关闭事件参数。</param>
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
