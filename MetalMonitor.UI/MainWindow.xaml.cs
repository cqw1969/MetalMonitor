using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI
{
    public partial class MainWindow : Window
    {
        private MainViewModel? _viewModel;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}