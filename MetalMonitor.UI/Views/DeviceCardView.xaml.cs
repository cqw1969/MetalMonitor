using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetalMonitor.UI.Views
{
    public partial class DeviceCardView : UserControl
    {
        public event EventHandler<DeviceCardClickedEventArgs>? CardClicked;

        public DeviceCardView()
        {
            InitializeComponent();
            RootGrid.PreviewMouseLeftButtonDown += OnCardClicked;
        }

        private void OnCardClicked(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (DataContext is DeviceCardViewModel viewModel)
            {
                CardClicked?.Invoke(this, new DeviceCardClickedEventArgs(viewModel.Device));
            }
        }
    }

    public class DeviceCardClickedEventArgs : EventArgs
    {
        public Device Device { get; }

        public DeviceCardClickedEventArgs(Device device)
        {
            Device = device;
        }
    }
}