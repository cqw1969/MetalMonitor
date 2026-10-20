using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using System.Windows;

//using System.Diagnostics;

namespace MetalMonitor.UI.ViewModels
{
    /// <summary>
    /// 主界面视图模型：通过每秒触发的定时器调用 <see cref="IDeviceService.UpdateDevicesAsync"/> 刷新设备数据，
    /// 聚合各设备的 <see cref="DeviceCardViewModel"/> 卡片集合，管理指标切换、数据源切换、数据库路径选择与报警日志，
    /// 并实现定时器防重入与资源释放。
    /// </summary>
    public class MainViewModel : IDisposable, INotifyPropertyChanged
    {
        /// <summary>主界面设备卡片视图模型集合，与设备服务中的设备列表保持同步（增删设备时联动）。</summary>
        public ObservableCollection<DeviceCardViewModel> DeviceCardViewModels { get; set; }

        /// <summary>设备数据服务，负责设备采集、数据源切换、指标配置读取与历史点上限。</summary>
        private readonly IDeviceService _deviceService;
        /// <summary>每秒触发一次的数据刷新定时器（<see cref="System.Timers.Timer"/>，在线程池线程回调）。</summary>
        private readonly System.Timers.Timer _dataTimer;
        /// <summary>报警服务，用于订阅报警开始/恢复事件。</summary>
        private readonly IAlarmService _alarmService;
        /// <summary>数据库服务，提供数据库路径查询与更改等能力。</summary>
        private readonly IDatabaseService _databaseService;
        /// <summary>定时器回调防重入标记：0 空闲、1 正在更新，通过 <see cref="Interlocked"/> 原子操作访问。</summary>
        private int _isUpdating = 0; // 防重入标记

        /// <summary>当前选中设备的详情视图模型（当前由卡片点击打开独立窗口，该属性保留绑定用途）。</summary>
        private DeviceDetailViewModel? _selectedDeviceDetail;

        /// <summary>
        /// 获取或设置当前选中设备的详情视图模型；变更时通知 UI。
        /// </summary>
        public DeviceDetailViewModel? SelectedDeviceDetail
        {
            get => _selectedDeviceDetail;
            set
            {
                _selectedDeviceDetail = value;
                OnPropertyChanged();
            }
        }

        /// <summary>报警与异常日志文本集合（带时间戳），绑定主界面日志列表，仅允许在 UI 线程追加。</summary>
        public ObservableCollection<string> AlarmLogs { get; set; } = new();

        /// <summary>可选指标配置列表（含停用指标，停用后卡片显示灰色失效态），绑定主界面指标下拉框。</summary>
        // 可选指标列表（含停用指标，停用后卡片显示灰色失效态）
        public ObservableCollection<MetricConfig> AvailableMetrics { get; } = new();

        /// <summary>当前所有卡片统一展示的指标名称（ backing field ），默认 Temperature。</summary>
        private string _selectedMetricName = "Temperature";
        /// <summary>
        /// 获取或设置当前展示的指标名称；变更时同步切换所有设备卡片展示的指标。
        /// </summary>
        public string SelectedMetricName
        {
            get => _selectedMetricName;
            set
            {
                if (_selectedMetricName == value) return;
                _selectedMetricName = value;
                OnPropertyChanged();
                // 切换所有卡片展示的指标
                foreach (var card in DeviceCardViewModels)
                {
                    card.MetricName = value;
                }
            }
        }

        /// <summary>获取当前正在使用的数据来源名称（直接取自设备服务）。</summary>
        public string DataSource => _deviceService.DataSource;

        /// <summary>获取可选数据来源名称数组（绑定数据源下拉框选项）。</summary>
        public string[] DataSourceOptions => _deviceService.AvailableDataSources;

        /// <summary>下拉框当前选中的数据来源名称；setter 中触发异步切换。</summary>
        private string _selectedDataSource;
        /// <summary>
        /// 获取或设置选中的数据来源；与现值不同时异步调用设备服务执行切换。
        /// </summary>
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

        /// <summary>获取当前数据库文件存储路径（直接取自数据库服务）。</summary>
        public string DatabasePath => _databaseService.DatabasePath;

        /// <summary>属性变更通知事件，供 WPF 绑定系统订阅。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 初始化 <see cref="MainViewModel"/> 的新实例：注入各服务、订阅报警状态变更事件、
        /// 初始化卡片集合，并创建并启动间隔 1 秒、自动重置的数据刷新定时器。
        /// </summary>
        /// <param name="deviceService">设备数据服务。</param>
        /// <param name="alarmService">报警服务。</param>
        /// <param name="databaseService">数据库服务。</param>
        /// <remarks>
        /// 构造时订阅 <see cref="IAlarmService.AlarmStateChanged"/>（在 <see cref="Dispose(bool)"/> 中退订）；
        /// 定时器 <see cref="System.Timers.Timer.Elapsed"/> 在线程池线程触发，回调内部自行做防重入与 UI 线程封送。
        /// </remarks>
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

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件；属性名缺省时由调用方成员名自动填充。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称，null 时由 <see cref="CallerMemberNameAttribute"/> 自动传入。</param>
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 定时器每秒回调：拉取设备最新数据，并在 UI 线程同步卡片集合（移除已删除设备、追加新设备）、
        /// 刷新指标下拉列表与各卡片的坐标范围/启停状态。
        /// </summary>
        /// <param name="sender">事件发送者（定时器实例）。</param>
        /// <param name="e">定时器触发事件参数（触发时刻等）。</param>
        /// <remarks>
        /// 本回调在线程池线程执行：使用 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 做防重入，
        /// 上一次更新未完成时直接跳过本次；所有对 <see cref="DeviceCardViewModels"/>、<see cref="AlarmLogs"/>
        /// 等可观察集合的访问均通过 <see cref="System.Windows.Threading.Dispatcher.BeginInvoke"/> 封送回 UI 线程，
        /// Dispatcher 回调内部单独 try/catch，避免未捕获异常导致无报错闪退。
        /// </remarks>
        private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 防重入：上一次未完成则跳过本次
            if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0)
                return;

            try
            {
                await _deviceService.UpdateDevicesAsync();

                // CS4014 修复：BeginInvoke 返回 DispatcherOperation，丢弃返回值是故意为之
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var currentDevices = _deviceService.GetDevices();
                        var currentIds = currentDevices.Select(d => d.Id).ToHashSet();

                        // 移除已删除设备的卡片（只释放被删的，保留其他卡片历史曲线）
                        for (int i = DeviceCardViewModels.Count - 1; i >= 0; i--)
                        {
                            if (!currentIds.Contains(DeviceCardViewModels[i].Device.Id))
                            {
                                DeviceCardViewModels[i].Dispose();
                                DeviceCardViewModels.RemoveAt(i);
                            }
                        }

                        // 追加新增设备的卡片
                        var existingIds = DeviceCardViewModels.Select(c => c.Device.Id).ToHashSet();
                        foreach (var device in currentDevices)
                        {
                            if (!existingIds.Contains(device.Id))
                            {
                                DeviceCardViewModels.Add(new DeviceCardViewModel(device, _deviceService)
                                {
                                    MetricName = _selectedMetricName
                                });
                            }
                        }

                        // 刷新指标下拉列表（配置变更后生效）
                        RefreshAvailableMetrics();

                        // 刷新设备卡片 Y 轴范围与启停状态（设置变更后生效）
                        foreach (var card in DeviceCardViewModels)
                        {
                            card.ApplyMetricLimits();
                        }
                    }
                    catch (Exception dex)
                    {
                        // Dispatcher 回调中的异常若不捕获会导致无报错闪退
                        AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✗ 界面刷新异常：{dex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✗ 数据更新异常：{ex.Message}");
                });
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdating, 0);
            }
        }

        /// <summary>
        /// 报警状态变更回调：根据报警开始/恢复生成带时间戳的日志文本并追加到报警日志集合。
        /// </summary>
        /// <param name="deviceName">发生报警状态变化的设备名称。</param>
        /// <param name="metricName">发生报警状态变化的指标名称。</param>
        /// <param name="isAlarming">true 表示进入报警状态，false 表示恢复正常。</param>
        /// <param name="value">触发本次状态变化时的指标值。</param>
        /// <remarks>该事件可能由后台线程触发，追加日志前通过 Dispatcher 封送回 UI 线程执行。</remarks>
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

        /// <summary>
        /// 设备卡片点击入口：为指定设备创建设备详情视图模型并赋值给 <see cref="SelectedDeviceDetail"/>。
        /// </summary>
        /// <param name="device">被点击卡片对应的设备。</param>
        public void OnDeviceCardClicked(Device device)
        {
            SelectedDeviceDetail = new DeviceDetailViewModel(device, _databaseService);
        }

        /// <summary>
        /// 获取持有的数据库服务实例，供窗口 code-behind 创建子视图模型时复用。
        /// </summary>
        /// <returns>当前 <see cref="IDatabaseService"/> 实例。</returns>
        public IDatabaseService GetDatabaseService()
        {
            return _databaseService;
        }

        /// <summary>
        /// 获取持有的设备服务实例，供窗口 code-behind 创建子视图模型时复用。
        /// </summary>
        /// <returns>当前 <see cref="IDeviceService"/> 实例。</returns>
        public IDeviceService GetDeviceService()
        {
            return _deviceService;
        }

        /// <summary>
        /// 检测指标配置变化并刷新 <see cref="AvailableMetrics"/> 下拉列表（配置增删/改名后自动同步）；
        /// 当前选中指标已不存在时回退到第一个指标。
        /// </summary>
        // 检测指标配置变化并刷新下拉列表（配置增删/改名后自动同步）
        private void RefreshAvailableMetrics()
        {
            var configs = _deviceService.GetMetricConfigs();
            bool changed = configs.Count != AvailableMetrics.Count
                || configs.Any(c => !AvailableMetrics.Any(a => a.Name == c.Name));

            if (!changed) return;

            AvailableMetrics.Clear();
            foreach (var c in configs)
                AvailableMetrics.Add(c);

            // 当前选中的指标已不存在时，回退到第一个指标
            if (!configs.Any(c => c.Name == _selectedMetricName) && configs.Count > 0)
            {
                SelectedMetricName = configs[0].Name;
            }
        }

        /// <summary>
        /// 切换数据来源：调用设备服务执行切换，并根据切换结果记录成功/失败日志；
        /// 切换失败（如 MQTT 连接失败）时将下拉框回退为实际数据源。
        /// </summary>
        /// <param name="source">目标数据来源名称。</param>
        /// <returns>表示异步切换操作的任务。</returns>
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

        /// <summary>
        /// 弹出文件夹选择对话框供用户选择数据库存储路径，选中后异步通知数据库服务更改路径并刷新绑定与日志。
        /// </summary>
        /// <remarks>async void 仅用于 UI 事件直接调用；用户取消选择时不做任何更改。</remarks>
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

        /// <summary>
        /// 通过 Windows Vista 风格的 IFileOpenDialog COM 接口显示“选择文件夹”对话框。
        /// </summary>
        /// <param name="title">对话框标题文本。</param>
        /// <param name="initialPath">对话框初始定位目录；为 null 或空时使用系统默认位置。</param>
        /// <returns>用户选中目录的文件系统绝对路径；用户取消或未取得结果时返回 null。</returns>
        /// <remarks>
        /// 用户取消对话框时 COM interop 会抛出 <see cref="COMException"/>（HRESULT 为 <see cref="ErrorCancelled"/>），
        /// 此处将其捕获并视为正常取消返回 null；所有 COM 对象使用后显式 <see cref="Marshal.ReleaseComObject"/> 释放。
        /// </remarks>
        private string? BrowseForFolder(string title, string? initialPath = null)
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

            // 用户取消对话框时 Show 返回 ERROR_CANCELLED (0x800704C7)，
            // COM interop 会将其转为异常抛出，这里捕获并视为正常取消
            try
            {
                dialog.Show(IntPtr.Zero);
            }
            catch (COMException ex) when (ex.ErrorCode == ErrorCancelled)
            {
                Marshal.ReleaseComObject(dialog);
                return null;
            }

            dialog.GetResult(out var item);
            if (item == null)
            {
                Marshal.ReleaseComObject(dialog);
                return null;
            }

            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(dialog);

            return path;
        }

        /// <summary>
        /// Shell32 P/Invoke：由文件系统绝对路径创建一个 IShellItem 实例，用于把初始目录传给文件夹选择对话框。
        /// </summary>
        /// <param name="pszPath">目录或文件的绝对路径（解析名）。</param>
        /// <param name="pbc">绑定上下文指针，本应用不使用，传 <see cref="IntPtr.Zero"/>。</param>
        /// <param name="riid">请求接口的 IID（IShellItem 的 GUID）。</param>
        /// <param name="ppv">输出创建得到的 IShellItem 接口对象。</param>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In] string pszPath,
            [In] IntPtr pbc,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        /// <summary>
        /// 由文件系统路径创建 IShellItem COM 对象的托管包装。
        /// </summary>
        /// <param name="path">目标目录的绝对路径。</param>
        /// <returns>对应路径的 <see cref="IShellItem"/> 实例。</returns>
        private IShellItem ShellItemFromParsingName(string path)
        {
            var guid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var item);
            return item;
        }

        /// <summary>用户取消通用文件对话框时 Shell 返回的 HRESULT（0x800704C7），对应 winerror.h 的 ERROR_CANCELLED。</summary>
        // 用户取消对话框时 Shell 返回的 HRESULT，对应 winerror.h 的 ERROR_CANCELLED
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        /// <summary>
        /// Windows 通用文件打开对话框（IFileOpenDialog）的 COM 可创建类（CLSID_FileOpenDialog），仅用于 COM interop 声明。
        /// </summary>
        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        /// <summary>
        /// IFileOpenDialog COM 接口的托管互操作声明（方法顺序须与原生 vtable 严格一致），本应用用于弹出文件夹选择对话框。
        /// </summary>
        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            /// <summary>显示模态对话框。</summary>
            /// <param name="parent">父窗口句柄；无父窗口时传 <see cref="IntPtr.Zero"/>。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Show([In] IntPtr parent);

            /// <summary>设置文件类型过滤器列表。</summary>
            /// <param name="cFileTypes">过滤器项数量。</param>
            /// <param name="rgFilterSpec">过滤器规格数组的非托管指针。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileTypes([In] uint cFileTypes, [In] IntPtr rgFilterSpec);

            /// <summary>设置当前选中的文件类型过滤器索引。</summary>
            /// <param name="iFileType">过滤器从 1 开始的索引。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileTypeIndex([In] uint iFileType);

            /// <summary>获取当前选中的文件类型过滤器索引。</summary>
            /// <param name="piFileType">输出过滤器索引。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetFileTypeIndex(out uint piFileType);

            /// <summary>注册对话框事件接收对象。</summary>
            /// <param name="pfde">事件接收对象指针。</param>
            /// <param name="pdwCookie">输出用于退订的 Cookie。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Advise([In] IntPtr pfde, out uint pdwCookie);

            /// <summary>退订此前通过 Advise 注册的事件接收对象。</summary>
            /// <param name="dwCookie">Advise 返回的 Cookie。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Unadvise([In] uint dwCookie);

            /// <summary>设置对话框选项标志（如选择文件夹、强制文件系统）。</summary>
            /// <param name="fos"><see cref="FOS"/> 选项标志组合。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetOptions([In] FOS fos);

            /// <summary>获取对话框当前选项标志。</summary>
            /// <param name="pfos">输出当前 <see cref="FOS"/> 标志组合。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetOptions(out FOS pfos);

            /// <summary>设置对话框默认定位文件夹（若记住的最近文件夹不可用则使用）。</summary>
            /// <param name="psi">默认文件夹的 IShellItem。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetDefaultFolder([In] IShellItem psi);

            /// <summary>强制设置对话框本次打开时定位的文件夹。</summary>
            /// <param name="psi">目标文件夹的 IShellItem。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFolder([In] IShellItem psi);

            /// <summary>获取对话框当前定位的文件夹。</summary>
            /// <param name="ppsi">输出当前文件夹的 IShellItem。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetFolder(out IShellItem ppsi);

            /// <summary>获取对话框中当前选中项（对话框未关闭时也可调用）。</summary>
            /// <param name="ppsi">输出当前选中项的 IShellItem。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetCurrentSelection(out IShellItem ppsi);

            /// <summary>设置文件名编辑框的初始文本。</summary>
            /// <param name="pszName">初始文件名。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);

            /// <summary>获取文件名编辑框当前文本。</summary>
            /// <param name="pszName">输出当前文件名。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

            /// <summary>设置对话框标题文本。</summary>
            /// <param name="pszTitle">标题文本。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

            /// <summary>设置“确定”按钮文本。</summary>
            /// <param name="pszText">按钮文本。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

            /// <summary>设置文件名编辑框旁的标签文本。</summary>
            /// <param name="pszLabel">标签文本。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

            /// <summary>对话框关闭后获取用户选择的结果项。</summary>
            /// <param name="ppsi">输出选中项的 IShellItem。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetResult(out IShellItem ppsi);

            /// <summary>在对话框导航窗格中添加一个自定义位置。</summary>
            /// <param name="psi">自定义位置的 IShellItem。</param>
            /// <param name="fdap">位置放置标识（FDAP 枚举值）。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void AddPlace([In] IShellItem psi, int fdap);

            /// <summary>设置默认文件扩展名。</summary>
            /// <param name="pszDefaultExtension">默认扩展名文本（可含或不含句点）。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

            /// <summary>以指定 HRESULT 关闭对话框。</summary>
            /// <param name="hr">关闭结果 HRESULT。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Close(int hr);

            /// <summary>设置 GUID 以将对话框状态（如最近位置）与特定调用方关联。</summary>
            /// <param name="guid">客户端 GUID。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetClientGuid([In] ref Guid guid);

            /// <summary>清除持久化的对话框状态信息。</summary>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void ClearClientData();

            /// <summary>设置对话框过滤器（IShellItemFilter）。</summary>
            /// <param name="pFilter">过滤器对象指针。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetFilter([In] IntPtr pFilter);

            /// <summary>多选模式下获取全部选中项。</summary>
            /// <param name="ppenum">输出 IShellItemArray 枚举器指针。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetResults(out IntPtr ppenum);

            /// <summary>获取当前在对话框中选中的多项（未关闭时）。</summary>
            /// <param name="ppsai">输出选中项数组指针。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetSelectedItems(out IntPtr ppsai);
        }

        /// <summary>
        /// IShellItem COM 接口的托管互操作声明，表示 Shell 命名空间中的一个项（此处为文件夹），用于读取其文件系统路径。
        /// </summary>
        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            /// <summary>将 Shell 项绑定到指定处理器以获取目标接口。</summary>
            /// <param name="pbc">绑定上下文指针。</param>
            /// <param name="bhid">处理器类别的 GUID。</param>
            /// <param name="riid">请求接口的 IID。</param>
            /// <param name="ppv">输出绑定得到的接口指针。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out IntPtr ppv);

            /// <summary>获取父级 Shell 项。</summary>
            /// <param name="ppsi">输出父项的 IShellItem。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetParent([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

            /// <summary>按指定显示名称类型获取项的名称（本应用用其获取文件系统绝对路径）。</summary>
            /// <param name="sigdnName"><see cref="SIGDN"/> 名称类型。</param>
            /// <param name="ppszName">输出名称字符串。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetDisplayName([In] SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

            /// <summary>获取 Shell 项的指定属性位。</summary>
            /// <param name="sfgaoMask">请求的 SFGAO 属性掩码。</param>
            /// <param name="psfgaoAttribs">输出实际具备的属性位。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);

            /// <summary>与另一个 Shell 项比较。</summary>
            /// <param name="psi">用于比较的另一个 Shell 项。</param>
            /// <param name="hint">比较提示（SICHINT 标志）。</param>
            /// <param name="piOrder">输出比较结果：负数表示此项在前，正数在后，0 表示相同。</param>
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Compare([In] IShellItem psi, [In] uint hint, out int piOrder);
        }

        /// <summary>
        /// 通用文件对话框选项标志（对应原生 FILEOPENDIALOGOPTIONS / FOS）。
        /// </summary>
        [Flags]
        private enum FOS
        {
            /// <summary>覆盖已有文件时提示。</summary>
            FOS_OVERWRITEPROMPT = 0x2,
            /// <summary>严格按文件类型过滤器限制。</summary>
            FOS_STRICTFILETYPES = 0x4,
            /// <summary>不改变当前工作目录。</summary>
            FOS_NOCHANGEDIR = 0x8,
            /// <summary>选择文件夹而非文件。</summary>
            FOS_PICKFOLDERS = 0x20,
            /// <summary>仅返回文件系统中的真实项。</summary>
            FOS_FORCEFILESYSTEM = 0x40,
            /// <summary>允许选择非存储类 Shell 项。</summary>
            FOS_ALLNONSTORAGEITEMS = 0x80,
            /// <summary>不校验文件内容（防特殊文件校验失败）。</summary>
            FOS_NOVALIDATE = 0x100,
            /// <summary>允许多选。</summary>
            FOS_ALLOWMULTISELECT = 0x200,
            /// <summary>路径必须已存在。</summary>
            FOS_PATHMUSTEXIST = 0x800,
            /// <summary>文件必须已存在。</summary>
            FOS_FILEMUSTEXIST = 0x1000,
            /// <summary>目标不存在时提示创建。</summary>
            FOS_CREATEPROMPT = 0x2000,
            /// <summary>共享冲突时重试并给出提示。</summary>
            FOS_SHAREAWARE = 0x4000,
            /// <summary>返回的选中项不允许只读。</summary>
            FOS_NOREADONLYRETURN = 0x8000,
            /// <summary>不测试创建文件是否成功。</summary>
            FOS_NOTESTFILECREATE = 0x10000,
            /// <summary>隐藏最近使用位置列表。</summary>
            FOS_HIDEMRUPLACES = 0x20000,
            /// <summary>隐藏固定到导航窗格的位置。</summary>
            FOS_HIDEPINNEDPLACES = 0x40000,
            /// <summary>不解析快捷方式，直接返回 .lnk 文件本身。</summary>
            FOS_NODEREFERENCELINKS = 0x100000,
            /// <summary>“确定”按钮需要用户进行交互后才可用。</summary>
            FOS_OKBUTTONNEEDSINTERACTION = 0x200000,
            /// <summary>不将本次选择加入最近使用记录。</summary>
            FOS_DONTADDTORECENT = 0x2000000,
            /// <summary>强制显示预览窗格。</summary>
            FOS_FORCEPREVIEWPANEON = 0x4000000,
            /// <summary>支持按流方式传递的项。</summary>
            FOS_SUPPORTSTREAMABLEITEMS = 0x8000000
        }

        /// <summary>
        /// Shell 项显示名称类型（对应原生 SIGDN），本应用使用文件系统路径类型。
        /// </summary>
        private enum SIGDN : uint
        {
            /// <summary>桌面相对的可编辑名称。</summary>
            SIGDN_DESKTOPABSOLUTEEDITING = 0x8004c000,
            /// <summary>桌面相对的解析名。</summary>
            SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000,
            /// <summary>文件系统绝对路径。</summary>
            SIGDN_FILESYSPATH = 0x80058000,
            /// <summary>常规显示名称。</summary>
            SIGDN_NORMALDISPLAY = 0,
            /// <summary>相对父级的名称。</summary>
            SIGDN_PARENTRELATIVE = 0x80080001,
            /// <summary>相对父级、可编辑的名称。</summary>
            SIGDN_PARENTRELATIVEEDITING = 0x80031001,
            /// <summary>用于地址栏的相对父级名称。</summary>
            SIGDN_PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
            /// <summary>相对父级的解析名。</summary>
            SIGDN_PARENTRELATIVEPARSING = 0x80018001,
            /// <summary>URL 形式的名称。</summary>
            SIGDN_URL = 0x80068000
        }

        #region IDisposable

        /// <summary>释放标记：true 表示已执行过释放逻辑，防止重复释放。</summary>
        private bool _disposed = false;

        /// <summary>
        /// 释放本视图模型占用的资源：停止并释放定时器、退订报警事件、释放全部设备卡片视图模型。
        /// </summary>
        // 实现 IDisposable 接口，用于释放 Timer 和取消事件订阅
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 受保护的释放实现：保证释放逻辑幂等，仅在显式释放（<paramref name="disposing"/> 为 true）时清理托管资源。
        /// </summary>
        /// <param name="disposing">true 表示由 <see cref="Dispose"/> 显式调用，可清理托管资源；false 表示由终结器调用。</param>
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

        /// <summary>终结器：在未显式调用 <see cref="Dispose"/> 时以 disposing=false 执行兜底释放。</summary>
        ~MainViewModel()
        {
            Dispose(false);
        }

        #endregion
    }
}