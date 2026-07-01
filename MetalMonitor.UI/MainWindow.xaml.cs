using MetalMonitor.UI.ViewModels;
using System.Windows;

namespace MetalMonitor.UI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // 把 ViewModel 挂到窗口的数据上下文上
            DataContext = new MainViewModel();
        }
    }
}