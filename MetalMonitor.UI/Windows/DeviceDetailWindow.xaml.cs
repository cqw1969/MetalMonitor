using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI.Windows
{
    public partial class DeviceDetailWindow : Window
    {
        private DeviceDetailViewModel? _viewModel;

        public DeviceDetailWindow(DeviceDetailViewModel viewModel)
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

        private async void OnQueryButtonClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.LoadHistoricalDataAsync();
            }
        }
    }
}