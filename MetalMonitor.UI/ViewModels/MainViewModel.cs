using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Timers;
using System.Windows;

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
        /// <summary>配置服务（DI 单例），提供心跳间隔等运行参数。</summary>
        private readonly ConfigService _configService;
        /// <summary>定时器回调防重入标记：0 空闲、1 正在更新，通过 <see cref="Interlocked"/> 原子操作访问。</summary>
        private int _isUpdating = 0;

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
        public ObservableCollection<string> AlarmLogs { get; set; } = new BoundedObservableCollection<string>(200);

        /// <summary>可选指标配置列表（含停用指标，停用后卡片显示灰色失效态），绑定主界面指标下拉框。</summary>
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

        /// <summary>当前数据源运行状态（枚举转发，供状态行颜色触发器按值映射）。</summary>
        public DataSourceState CurrentDataSourceState => _deviceService.State;

        /// <summary>数据源状态显示文本（正常 / 连接失败 / 不可用）。</summary>
        public string DataSourceStateText => ToStateText(_deviceService.State);

        /// <summary>当前串口链路健康（枚举转发，供状态行颜色触发器按值映射）。</summary>
        public LinkHealth CurrentLinkHealth => _deviceService.LinkHealth;

        /// <summary>链路健康显示文本（健康 / 等待首帧 / 有字节无有效帧 / 无信号 / 未连接）。</summary>
        public string LinkHealthText => ToLinkHealthText(_deviceService.LinkHealth);

        /// <summary>数据源状态枚举 → 中文短词（Core 只给枚举，文案翻译是 UI 层职责）。</summary>
        private static string ToStateText(DataSourceState state) => state switch
        {
            DataSourceState.Online => "正常",
            DataSourceState.Faulted => "连接失败",
            DataSourceState.Simulated => "模拟运行",
            DataSourceState.Searching => "寻找设备中",
            _ => "不可用"
        };

        /// <summary>链路健康枚举 → 中文短词（Core 只给枚举，文案翻译是 UI 层职责）。</summary>
        private static string ToLinkHealthText(LinkHealth health) => health switch
        {
            LinkHealth.Healthy => "健康",
            LinkHealth.Handshaking => "等待验证",
            LinkHealth.Recovering => "等待数据",
            LinkHealth.Reconnecting => "重连中",
            LinkHealth.NoValidFrame => "有字节无有效帧",
            LinkHealth.NoSignal => "无信号",
            _ => "未连接"
        };

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
        /// <param name="configService">配置服务（DI 单例），提供心跳间隔等运行参数。</param>
        /// <remarks>
        /// 构造时订阅 <see cref="IAlarmService.AlarmStateChanged"/>（在 <see cref="Dispose(bool)"/> 中退订）；
        /// 定时器 <see cref="System.Timers.Timer.Elapsed"/> 在线程池线程触发，回调内部自行做防重入与 UI 线程封送。
        /// </remarks>
        public MainViewModel(IDeviceService deviceService, IAlarmService alarmService, IDatabaseService databaseService, ConfigService configService)
        {
            _deviceService = deviceService;
            _alarmService = alarmService;
            _databaseService = databaseService;
            _configService = configService;
            _selectedDataSource = _deviceService.DataSource;

            _alarmService.AlarmStateChanged += OnAlarmStateChanged;
            // 数据源状态变更（成功/故障/不可用）统一走事件提示，不在切源代码里各写一份日志
            _deviceService.DataSourceStateChanged += OnDataSourceStateChanged;
            // 链路健康变更（跃迁才广播）：驱动顶栏"链路"状态行
            _deviceService.LinkHealthChanged += OnLinkHealthChanged;

            DeviceCardViewModels = new ObservableCollection<DeviceCardViewModel>();

            // 心跳间隔取配置值；配置非法（<=0）时回退 1000ms，避免 Timer.Interval 抛 ArgumentException
            int refreshInterval = _configService.RefreshInterval > 0 ? _configService.RefreshInterval : 1000;
            _dataTimer = new System.Timers.Timer(refreshInterval);
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
        /// 切换数据来源：调用设备服务执行切换，并刷新数据来源显示。
        /// </summary>
        /// <param name="source">目标数据来源名称。</param>
        /// <returns>表示异步切换操作的任务。</returns>
        /// <remarks>
        /// 失败不再回退下拉框：用户的选择保持不变，失败原因由 <see cref="OnDataSourceStateChanged"/> 写入日志区。
        /// </remarks>
        public async Task SwitchDataSourceAsync(string source)
        {
            await _deviceService.SetDataSourceAsync(source);
            OnPropertyChanged(nameof(DataSource));
        }

        /// <summary>
        /// 数据源状态变更回调：正常记成功，故障/不可用记明原因；状态栏提示不静默。
        /// </summary>
        /// <param name="sender">事件发送者（设备服务实例）。</param>
        /// <param name="e">状态变更参数（新状态、数据源显示名、失败原因）。</param>
        /// <remarks>Core 侧全程 ConfigureAwait(false)，事件可能落在后台线程；AlarmLogs 为 UI 集合，需封送回 UI 线程。</remarks>
        private void OnDataSourceStateChanged(object? sender, DataSourceStateChangedEventArgs e)
        {
            void Apply()
            {
                OnPropertyChanged(nameof(CurrentDataSourceState));
                OnPropertyChanged(nameof(DataSourceStateText));
                if (e.State == DataSourceState.Online)
                {
                    AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 数据来源已切换为：{e.SourceDisplay}");
                }
                else
                {
                    string stateText = e.State == DataSourceState.Unavailable ? "不可用" : "连接失败";
                    AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✗ {e.SourceDisplay} {stateText}：{e.Reason}");
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(Apply);
            }
            else
            {
                Apply();
            }
        }

        /// <summary>
        /// 链路健康变更回调：刷新顶栏"链路"状态行的绑定属性。
        /// </summary>
        /// <param name="sender">事件发送者（设备服务实例）。</param>
        /// <param name="e">链路健康变更参数（新状态、数据源显示名、原因）。</param>
        /// <remarks>
        /// 事件可能由串口后台线程或心跳线程触发，属性通知需封送回 UI 线程；
        /// Core 侧已同值短路，仅跃迁触发，不会每秒轰炸。
        /// </remarks>
        private void OnLinkHealthChanged(object? sender, LinkHealthChangedEventArgs e)
        {
            void Apply()
            {
                OnPropertyChanged(nameof(CurrentLinkHealth));
                OnPropertyChanged(nameof(LinkHealthText));
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(Apply);
            }
            else
            {
                Apply();
            }
        }

        /// <summary>
        /// 弹出文件夹选择对话框供用户选择数据库存储路径，选中后异步通知数据库服务更改路径并刷新绑定与日志。
        /// </summary>
        /// <remarks>async void 仅用于 UI 事件直接调用；用户取消选择时不做任何更改。</remarks>
        public async void SelectDatabasePath()
        {
            // 原实现：手写 IFileOpenDialog COM 互操作（约 350 行），已删；回退见 git 历史
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "请选择数据库存储路径",
                InitialDirectory = string.IsNullOrEmpty(DatabasePath) ? null : Path.GetDirectoryName(DatabasePath)
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
            {
                await _databaseService.ChangeDatabasePathAsync(dialog.FolderName);
                OnPropertyChanged(nameof(DatabasePath));
                AlarmLogs.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 数据库路径已更改为：{dialog.FolderName}");
            }
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

                // 取消数据源状态事件订阅
                _deviceService.DataSourceStateChanged -= OnDataSourceStateChanged;

                // 取消链路健康事件订阅
                _deviceService.LinkHealthChanged -= OnLinkHealthChanged;

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
