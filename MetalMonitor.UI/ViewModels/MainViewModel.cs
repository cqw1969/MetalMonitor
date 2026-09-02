using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using System.Windows;

//using System.Diagnostics;

namespace MetalMonitor.UI.ViewModels
{
    public class MainViewModel : IDisposable, INotifyPropertyChanged
    {
        public ObservableCollection<DeviceCardViewModel> DeviceCardViewModels { get; set; }

        private readonly IDeviceService _deviceService;
        private readonly System.Timers.Timer _dataTimer;
        private readonly IAlarmService _alarmService;
        private readonly IDatabaseService _databaseService;
        private int _isUpdating = 0; // 防重入标记

        private DeviceDetailViewModel? _selectedDeviceDetail;

        public DeviceDetailViewModel? SelectedDeviceDetail
        {
            get => _selectedDeviceDetail;
            set
            {
                _selectedDeviceDetail = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> AlarmLogs { get; set; } = new();

        public string DataSource => _deviceService.DataSource;

        public string[] DataSourceOptions => _deviceService.AvailableDataSources;

        private string _selectedDataSource;
        public string SelectedDataSource
        {
            get => _selectedDataSource;
            set
            {
                if (_selectedDataSource == value) return;
                _selectedDataSource = value;
                OnPropertyChanged();
                _ = SwitchDataSourceAsync(value);
            }
        }

        public string DatabasePath => _databaseService.DatabasePath;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(IDeviceService deviceService, IAlarmService alarmService, IDatabaseService databaseService)
        {
            _deviceService = deviceService;
            _alarmService = alarmService;
            _databaseService = databaseService;
            _selectedDataSource = _deviceService.DataSource;

            _alarmService.AlarmStateChanged += OnAlarmStateChanged;

            //Devices = new ObservableCollection<Device>();
            DeviceCardViewModels = new ObservableCollection<DeviceCardViewModel>();

            _dataTimer = new System.Timers.Timer(1000);
            _dataTimer.Elapsed += OnTimerElapsed;
            _dataTimer.AutoReset = true;
            _dataTimer.Start();
        }

        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 防重入：上一次未完成则跳过本次
            if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0)
                return;

            try
            {
                await _deviceService.UpdateDevicesAsync();

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var currentDevices = _deviceService.GetDevices();
                    if (DeviceCardViewModels.Count != currentDevices.Count)
                    {
                        DeviceCardViewModels.Clear();
                        foreach (var device in currentDevices)
                        {
                            DeviceCardViewModels.Add(new DeviceCardViewModel(device, _deviceService));
                        }
                    }

                    // 刷新设备卡片 Y 轴范围（设置变更后生效）
                    foreach (var card in DeviceCardViewModels)
                    {
                        card.ApplyMetricLimits();
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✗ 数据更新异常：{ex.Message}");
                });
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdating, 0);
            }
        }

        private void OnAlarmStateChanged(string deviceName, string metricName, bool isAlarming, double value)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                string log = isAlarming
                    ? $"[{DateTime.Now:HH:mm:ss}] ⚠ {deviceName} {metricName}报警！值：{value:F1}"
                    : $"[{DateTime.Now:HH:mm:ss}] ✓ {deviceName} {metricName}恢复正常，值：{value:F1}";
                AlarmLogs.Add(log);
            });
        }

        public void OnDeviceCardClicked(Device device)
        {
            SelectedDeviceDetail = new DeviceDetailViewModel(device, _databaseService);
        }

        public IDatabaseService GetDatabaseService()
        {
            return _databaseService;
        }

        public IDeviceService GetDeviceService()
        {
            return _deviceService;
        }

        public async Task SwitchDataSourceAsync(string source)
        {
            await _deviceService.SetDataSourceAsync(source);
            OnPropertyChanged(nameof(DataSource));

            if (_deviceService.DataSource != source)
            {
                // 切换失败（如 MQTT 连接失败），回退下拉框为实际数据源
                _selectedDataSource = _deviceService.DataSource;
                OnPropertyChanged(nameof(SelectedDataSource));
                AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✗ 切换到 {source} 失败，已回退为：{_deviceService.DataSource}");
            }
            else
            {
                AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 数据来源已切换为：{source}");
            }
        }

        public async void SelectDatabasePath()
        {
            var selectedPath = BrowseForFolder("请选择数据库存储路径", 
                string.IsNullOrEmpty(DatabasePath) ? null : Path.GetDirectoryName(DatabasePath));
            
            if (!string.IsNullOrEmpty(selectedPath))
            {
                await _databaseService.ChangeDatabasePathAsync(selectedPath);
                OnPropertyChanged(nameof(DatabasePath));
                AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 数据库路径已更改为：{selectedPath}");
            }
        }

        private string BrowseForFolder(string title, string initialPath = null)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);
            dialog.SetTitle(title);

            if (!string.IsNullOrEmpty(initialPath))
            {
                try
                {
                    var shellItem = ShellItemFromParsingName(initialPath);
                    if (shellItem != null)
                    {
                        dialog.SetFolder(shellItem);
                        Marshal.ReleaseComObject(shellItem);
                    }
                }
                catch
                {
                }
            }

            dialog.Show(IntPtr.Zero);

            dialog.GetResult(out var item);
            if (item == null) return null;

            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(dialog);

            return path;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In] string pszPath,
            [In] IntPtr pbc,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        private IShellItem ShellItemFromParsingName(string path)
        {
            var guid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var item);
            return item;
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Show([In] IntPtr parent);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileTypes([In] uint cFileTypes, [In] IntPtr rgFilterSpec);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileTypeIndex([In] uint iFileType);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetFileTypeIndex(out uint piFileType);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Advise([In] IntPtr pfde, out uint pdwCookie);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Unadvise([In] uint dwCookie);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetOptions([In] FOS fos);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetOptions(out FOS pfos);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetDefaultFolder([In] IShellItem psi);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFolder([In] IShellItem psi);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetFolder(out IShellItem ppsi);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetCurrentSelection(out IShellItem ppsi);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetResult(out IShellItem ppsi);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void AddPlace([In] IShellItem psi, int fdap);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Close(int hr);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetClientGuid([In] ref Guid guid);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void ClearClientData();

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFilter([In] IntPtr pFilter);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetResults(out IntPtr ppenum);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out IntPtr ppv);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetParent([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetDisplayName([In] SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Compare([In] IShellItem psi, [In] uint hint, out int piOrder);
        }

        [Flags]
        private enum FOS
        {
            FOS_OVERWRITEPROMPT = 0x2,
            FOS_STRICTFILETYPES = 0x4,
            FOS_NOCHANGEDIR = 0x8,
            FOS_PICKFOLDERS = 0x20,
            FOS_FORCEFILESYSTEM = 0x40,
            FOS_ALLNONSTORAGEITEMS = 0x80,
            FOS_NOVALIDATE = 0x100,
            FOS_ALLOWMULTISELECT = 0x200,
            FOS_PATHMUSTEXIST = 0x800,
            FOS_FILEMUSTEXIST = 0x1000,
            FOS_CREATEPROMPT = 0x2000,
            FOS_SHAREAWARE = 0x4000,
            FOS_NOREADONLYRETURN = 0x8000,
            FOS_NOTESTFILECREATE = 0x10000,
            FOS_HIDEMRUPLACES = 0x20000,
            FOS_HIDEPINNEDPLACES = 0x40000,
            FOS_NODEREFERENCELINKS = 0x100000,
            FOS_OKBUTTONNEEDSINTERACTION = 0x200000,
            FOS_DONTADDTORECENT = 0x2000000,
            FOS_FORCEPREVIEWPANEON = 0x4000000,
            FOS_SUPPORTSTREAMABLEITEMS = 0x8000000
        }

        private enum SIGDN : uint
        {
            SIGDN_DESKTOPABSOLUTEEDITING = 0x8004c000,
            SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000,
            SIGDN_FILESYSPATH = 0x80058000,
            SIGDN_NORMALDISPLAY = 0,
            SIGDN_PARENTRELATIVE = 0x80080001,
            SIGDN_PARENTRELATIVEEDITING = 0x80031001,
            SIGDN_PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
            SIGDN_PARENTRELATIVEPARSING = 0x80018001,
            SIGDN_URL = 0x80068000
        }

        #region IDisposable

        private bool _disposed = false;

        // 实现 IDisposable 接口，用于释放 Timer 和取消事件订阅
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // 停止并释放 Timer
                _dataTimer?.Stop();
                _dataTimer?.Dispose();

                // 取消报警服务事件订阅
                if (_alarmService != null)
                {
                    _alarmService.AlarmStateChanged -= OnAlarmStateChanged;
                }

                // 释放所有 DeviceCardViewModel
                foreach (var vm in DeviceCardViewModels)
                {
                    vm.Dispose();
                }
            }

            _disposed = true;
        }

        ~MainViewModel()
        {
            Dispose(false);
        }

        #endregion
    }
}