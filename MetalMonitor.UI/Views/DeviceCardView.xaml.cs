using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetalMonitor.UI.Views
{
    /// <summary>
    /// 单设备卡片视图 code-behind：在根网格上订阅预览鼠标左键事件，
    /// 点击卡片（含 LiveCharts 图表区域）时对外抛出 <see cref="CardClicked"/> 事件。
    /// </summary>
    public partial class DeviceCardView : UserControl
    {
        /// <summary>卡片被点击事件，携带卡片对应的设备，由主窗口订阅并打开设备详情窗口。</summary>
        public event EventHandler<DeviceCardClickedEventArgs>? CardClicked;

        /// <summary>
        /// 初始化 <see cref="DeviceCardView"/> 的新实例：加载 XAML 组件并订阅根网格的预览鼠标左键按下事件。
        /// </summary>
        /// <remarks>
        /// 订阅 <see cref="UIElement.PreviewMouseLeftButtonDown"/> 隧道事件而非冒泡的 MouseLeftButtonDown，
        /// 以规避卡片内 LiveCharts 图表控件吞掉鼠标事件导致点击不触发的问题。
        /// </remarks>
        public DeviceCardView()
        {
            InitializeComponent();
            RootGrid.PreviewMouseLeftButtonDown += OnCardClicked;
        }

        /// <summary>
        /// 卡片点击处理：标记事件已处理，并在 DataContext 为 <see cref="DeviceCardViewModel"/> 时触发 <see cref="CardClicked"/> 事件。
        /// </summary>
        /// <param name="sender">事件发送者（根网格）。</param>
        /// <param name="e">鼠标按钮事件参数。</param>
        private void OnCardClicked(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (DataContext is DeviceCardViewModel viewModel)
            {
                CardClicked?.Invoke(this, new DeviceCardClickedEventArgs(viewModel.Device));
            }
        }
    }

    /// <summary>
    /// 设备卡片点击事件参数：携带被点击卡片对应的设备。
    /// </summary>
    public sealed class DeviceCardClickedEventArgs : EventArgs
    {
        /// <summary>获取被点击卡片对应的设备。</summary>
        public Device Device { get; }

        /// <summary>
        /// 初始化 <see cref="DeviceCardClickedEventArgs"/> 的新实例。
        /// </summary>
        /// <param name="device">被点击卡片对应的设备。</param>
        public DeviceCardClickedEventArgs(Device device)
        {
            Device = device;
        }
    }
}