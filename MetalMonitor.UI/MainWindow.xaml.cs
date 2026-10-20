using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using MetalMonitor.UI.Views;
using MetalMonitor.UI.Windows;
using System.Windows;

namespace MetalMonitor.UI
{
    /// <summary>
    /// 主窗口 code-behind：持有 <see cref="MainViewModel"/> 作为 DataContext，
    /// 负责设备详情、设备管理、系统设置等非模态子窗口的打开，以及数据库路径选择的命令转发。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>主窗口的视图模型，窗口关闭时通过该字段释放其订阅与定时器。</summary>
        private MainViewModel? _viewModel;

        /// <summary>
        /// 初始化 <see cref="MainWindow"/> 的新实例，加载 XAML 组件并将指定视图模型设为 DataContext。
        /// </summary>
        /// <param name="viewModel">主界面视图模型（由 DI 容器解析传入）。</param>
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        /// <summary>
        /// 窗口关闭重写：释放主视图模型（停止定时器并退订事件），再调用基类关闭逻辑。
        /// </summary>
        /// <param name="e">窗口关闭事件参数。</param>
        protected override void OnClosed(EventArgs e)
        {
            _viewModel?.Dispose();
            base.OnClosed(e);
        }

        /// <summary>
        /// 设备卡片点击事件处理：以被点击设备为数据源创建设备详情视图模型，并以非模态方式打开详情窗口。
        /// </summary>
        /// <param name="sender">事件发送者（设备卡片视图）。</param>
        /// <param name="e">卡片点击事件参数，携带被点击的 <see cref="Device"/>。</param>
        /// <remarks>窗口以 <see cref="Window.Show"/> 非模态打开并将 Owner 设为本窗口，关闭时由子窗口负责重新激活主窗口。</remarks>
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

        /// <summary>
        /// “选择数据库路径”按钮点击处理：转发到主视图模型弹出文件夹选择对话框并更改数据库路径。
        /// </summary>
        /// <param name="sender">事件发送者（按钮）。</param>
        /// <param name="e">路由事件参数。</param>
        private void OnSelectDatabasePath(object sender, RoutedEventArgs e)
        {
            _viewModel?.SelectDatabasePath();
        }

        /// <summary>
        /// 打开设备管理窗口：创建设备管理视图模型并以非模态方式显示。
        /// </summary>
        /// <param name="sender">事件发送者（按钮/菜单项）。</param>
        /// <param name="e">路由事件参数。</param>
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

        /// <summary>
        /// 打开系统设置窗口：创建系统设置视图模型并以非模态方式显示。
        /// </summary>
        /// <param name="sender">事件发送者（按钮/菜单项）。</param>
        /// <param name="e">路由事件参数。</param>
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