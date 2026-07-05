using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI
{
    public partial class MainWindow : Window
    {
        private MainViewModel? _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            // 把 ViewModel 挂到窗口的数据上下文上
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            // 窗口关闭时释放 ViewModel 资源
            _viewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}