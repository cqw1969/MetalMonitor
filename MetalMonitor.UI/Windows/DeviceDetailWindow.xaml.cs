using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI.Windows
{
    /// <summary>
    /// 设备详情窗口 code-behind：持有 <see cref="DeviceDetailViewModel"/> 作为 DataContext，
    /// 在窗口激活时刷新指标配置、转发查询按钮点击，并在窗口关闭时释放视图模型并重新激活主窗口。
    /// </summary>
    public partial class DeviceDetailWindow : Window
    {
        /// <summary>本窗口的设备详情视图模型，关闭时用于释放事件订阅。</summary>
        private DeviceDetailViewModel? _viewModel;

        /// <summary>
        /// 初始化 <see cref="DeviceDetailWindow"/> 的新实例，加载 XAML 组件并将指定视图模型设为 DataContext。
        /// </summary>
        /// <param name="viewModel">设备详情视图模型。</param>
        public DeviceDetailWindow(DeviceDetailViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        /// <summary>
        /// 窗口内容加载完成事件处理：首次从数据库加载指标配置与历史数据。
        /// </summary>
        /// <param name="sender">事件发送者（本窗口）。</param>
        /// <param name="e">路由事件参数。</param>
        /// <remarks>
        /// async void 仅用于窗口事件订阅签名。首次加载从构造函数移到这里，是因为构造函数不可 async；
        /// 后续刷新仍由 <see cref="OnActivated"/> 负责。
        /// </remarks>
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.InitializeAsync();
            }
        }

        /// <summary>
        /// 窗口激活事件处理：异步刷新指标配置，使从系统设置改完阈值/最值后切回本窗口时立即生效。
        /// </summary>
        /// <param name="sender">事件发送者（本窗口）。</param>
        /// <param name="e">事件参数。</param>
        /// <remarks>async void 仅用于窗口事件订阅签名。</remarks>
        // 窗口激活时刷新指标配置（从系统设置改完阈值/最值后切回即生效）
        private async void OnActivated(object sender, EventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.RefreshConfigsAsync();
            }
        }

        /// <summary>
        /// 窗口关闭重写：释放详情视图模型（退订设备实时事件），并在非模态关闭后显式激活主窗口，避免焦点跳到其他后台窗口。
        /// </summary>
        /// <param name="e">窗口关闭事件参数。</param>
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

        /// <summary>
        /// “查询”按钮点击处理：按当前设备、指标与时间范围重新异步加载历史数据。
        /// </summary>
        /// <param name="sender">事件发送者（查询按钮）。</param>
        /// <param name="e">路由事件参数。</param>
        /// <remarks>async void 仅用于按钮事件订阅签名。</remarks>
        private async void OnQueryButtonClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.LoadHistoricalDataAsync();
            }
        }
    }
}