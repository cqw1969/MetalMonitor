using MetalMonitor.Core;
using MetalMonitor.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace MetalMonitor.UI.Views
{
    public partial class DeviceCardView : UserControl
    {
        //private DeviceCardViewModel? _viewModel;

        public DeviceCardView()
        {
            InitializeComponent();
            //DataContextChanged += OnDataContextChanged;
        }

        //private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        //{
        //    if (e.NewValue is Device device)
        //    {
        //        // 如果 ViewModel 不存在或对应的设备不同，就创建新的
        //        if (_viewModel == null || _viewModel.Device != device)
        //        {
        //            _viewModel = new DeviceCardViewModel(device);
        //            // 设置自身内部布局的 DataContext 为 ViewModel
        //            RootGrid.DataContext = _viewModel;
        //        }
        //    }
        //}

        // 当设备温度变化时，需要被通知以添加曲线点。
        // 我们可以监听 Device 的 PropertyChanged 事件。
        //public void UpdateTemperature()
        //{
        //    _viewModel?.AddTemperaturePoint(_viewModel.Device.Temperature);
        //}
    }
}