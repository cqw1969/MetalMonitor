using MetalMonitor.Core;
using System.Text.Json;
using System.Threading;
// 别名规避命名冲突：本类有同名属性 DataSource（string，对外 API），
// 类内直接写 DataSourceKind.Simulation 会被解析成"属性.Simulation"（成员查找优先于类型查找），
// 故枚举在类内一律写作 DataSourceKind
using DataSourceKind = MetalMonitor.Core.DataSource;

namespace MetalMonitor.core.Services
{
    /// <summary>
    /// 核心枢纽（架构定位）：数据管线中转站，UI 层只与本类对话（门面）。
    /// 三条数据通路（接入事件驱动 + 派发节拍驱动）：
    ///   接入段（各自线程）：解析/生成 → 写暂存区（同键 latest-wins）+ 追加落库缓冲（保真）
    ///   派发段（心跳 1Hz）：排空暂存区 → 报警判断 → SetMetricValue 触发 UI 事件（见 ApplyPendingSamples）
    ///   ①模拟数据 UpdateDeviceData（UI 线程，心跳驱动）
    ///   ②MQTT OnMqttMessageReceived（MQTTnet 后台线程，消息驱动）
    ///   ③串口（串口后台线程，帧驱动，见 SetDataSourceAsync 的 case DataSourceKind.Serial）
    /// 三个线程来源都要摸 _devices/_pendingBatch/_pendingSamples，这是 _devicesLock/_batchLock/_stagingLock 存在的原因。
    /// </summary>
    public class DeviceService : IDeviceService, IDisposable
    {
        // ===== 内存状态：设备"活体"集合 =====
        // 首次心跳时从数据库 Devices 表同步进来，之后与 UI 卡片共享同一批对象实例——
        // 值更新靠对象上的事件广播推给 UI，而不是每次重新查库
        /// <summary>设备"活体"集合：首次心跳时从数据库同步，之后与 UI 卡片共享同一批对象实例。</summary>
        private readonly List<Device> _devices;
        // D3：按设备名索引，把 UpdateDeviceFromMqtt 的 O(n) 线性查找降为 O(1)。
        // 与 _devices 同一把 _devicesLock 保护，且只在唯一的结构变更点
        // SyncDevicesFromDatabaseAsync（增删与改名都在其锁内）整体重建，故不会失效
        /// <summary>按设备名索引（与 <see cref="_devices"/> 同锁保护、同点重建），避免热路径线性扫集合。</summary>
        private readonly Dictionary<string, Device> _devicesByName = new();
        // 三条线程来源（UI 心跳 / MQTT 后台线程 / 未来串口线程）都会读写它，全靠这把锁串行化
        /// <summary>保护 <see cref="_devices"/> 集合结构与遍历的同步锁（对象内部状态由 Device 自身保证线程安全）。</summary>
        private readonly object _devicesLock = new();

        // ===== 协作服务（构造函数注入）=====
        // 报警服务必有；MQTT 与数据库可为 null——null = 该能力缺失，对应构造函数的降级形态
        /// <summary>报警判定服务（必有依赖），提供阈值判断与边沿通知。</summary>
        private readonly IAlarmService _alarmService;
        /// <summary>MQTT 数据源服务；为 null 表示该能力缺失，仅能走模拟数据通路。</summary>
        private readonly IMqttService? _mqttService;
        /// <summary>数据库落库服务；为 null 表示不落库，缓冲第三步静默失效。</summary>
        private readonly IDatabaseService? _databaseService;
        /// <summary>串口数据源服务（原始字节来源）；为 null 表示该能力缺失，串口通路整体跳过。</summary>
        private readonly ISerialService? _serialService;
        /// <summary>协议帧解析器（原始字节 → SensorDataFrame）；为 null 表示串口能力缺失。</summary>
        private readonly FrameParser? _frameParser;
        /// <summary>NODE_ID 帧路由器：构造时以 ConfigService 的启动只读路由表快照建立，运行中不变。</summary>
        private readonly FrameRouter? _frameRouter;

        // ===== 接入层暂存区（latest-wins）=====
        // 串口/MQTT 的后台线程收到样本后先写这里，心跳排空时统一执行"业务三步"，
        // 避免后台线程以收报频率直接触碰 UI 绑定的 Device（值写入会触发 UI 事件）
        /// <summary>线程安全暂存区：键为「设备对象 + 指标名」，同一键只保留最新值；由心跳排空后统一派发。</summary>
        private readonly Dictionary<(Device Device, string Metric), double> _pendingSamples = new();
        /// <summary>保护 <see cref="_pendingSamples"/> 的同步锁；接入层后台线程与心跳线程都会读写。</summary>
        private readonly object _stagingLock = new();

        // ===== 模拟数据通路专用 =====
        /// <summary>模拟数据随机数发生器，用于生成平滑随机游走。</summary>
        private readonly Random _random = new();
        /// <summary>模拟数据平滑缓存，键为"设备Id:指标名"，值为上一拍生成值。</summary>
        private readonly Dictionary<string, double> _lastSimValues = new(); // 模拟数据平滑用：上一值缓存

        // ===== 配置与运行状态旗标 =====
        /// <summary>配置服务（DI 单例注入，全局唯一副本，与 DatabaseService 共享同一实例）。</summary>
        private readonly ConfigService _configService;
        /// <summary>设备列表是否已完成首次数据库同步的旗标。</summary>
        private bool _devicesLoaded = false;                // 设备列表是否已完成首次同步
        /// <summary>指标配置快照（整体替换式更新）：驱动模拟数据量程并作为报警阈值的来源。</summary>
        private List<MetricConfig> _metricConfigs = new();  // 指标配置快照：驱动模拟量程与报警阈值
        /// <summary>按指标名索引（D3：把 GetMetricUnit 的 O(n) 线性查找降为 O(1)），与 <see cref="_metricConfigs"/> 同批整体替换。</summary>
        private Dictionary<string, MetricConfig> _metricConfigsByName = new();
        /// <summary>当前数据源，跨启动持久化在 config.json（存显示名，经 TryToDataSource 双向映射）。</summary>
        private DataSourceKind _selectedDataSource = DataSourceKind.Simulation; // 当前数据源；显示名映射见 DataSourceExtensions（单一事实源）
        /// <summary>数据源运行状态（三态）：只由 InitializeDataSourceAsync / SetDataSourceAsync 写入，单写者。</summary>
        private DataSourceState _state = DataSourceState.Online;
        /// <summary>最近一次数据源故障原因；状态正常时清空。</summary>
        private string? _lastDataSourceError;

        // ===== 串口链路健康 =====
        // 单调毫秒时间戳（Environment.TickCount64），跨串口后台线程与心跳线程读写，全部用 Volatile
        /// <summary>本次串口连接成功的时刻；0 表示未连接（Inactive 判据）。</summary>
        private long _serialConnectedAtMs;
        /// <summary>最近一次收到任意原始字节的时刻（乱码也算）；0 表示本次连接后从未收到。</summary>
        private long _lastSerialByteMs;
        /// <summary>最近一次收到有效帧（CRC 已通过）的时刻；0 表示从未收到。</summary>
        private long _lastSerialValidFrameMs;
        /// <summary>当前链路健康状态（单一权威源）；只在 EvaluateLinkHealthAt 中跃迁并广播。</summary>
        private volatile LinkHealth _linkHealth = LinkHealth.Inactive;
        /// <summary>链路健康跃迁锁：串口后台线程与心跳线程都会评估，保证"比较 + 写入"原子且只广播一次。</summary>
        private readonly object _linkHealthLock = new();

        // ===== 自动重连 =====
        /// <summary>重连循环 <u>取消</u> 令牌源：切源、Dispose、手动取消时置位让循环退出。</summary>
        private CancellationTokenSource? _reconnectCts;
        /// <summary>当前重连尝试次数（0 = 尚未开始）；仅在重连循环内递增。</summary>
        private int _reconnectAttempt;
        /// <summary>重连循环运行中标记（1 = 跑着）；用 Interlocked 原子读写，心跳线程与循环自身都能访问。</summary>
        private int _reconnectRunningFlag;
        /// <summary>退避等待的唤醒令牌源：端口重新出现时取消它，使重连循环立即重试而无需等待完整退避；仅在循环等待期间非空。</summary>
        private CancellationTokenSource? _reconnectWakeCts;
        /// <summary>
        /// 上一次轮询到的可用串口列表。
        /// 用于比对新老端口列表，确保“新设备接入”的事件只在状态跳变时触发（防止频繁打断退避重连逻辑）。因单心跳线程持有，无需加锁。
        /// </summary>
        private string[] _lastSeenSerialPorts = Array.Empty<string>();

        // 批量写入缓冲：达到 BatchSize 条后自动落盘，退出/切换时立即 flush
        /// <summary>待落库记录缓冲队列，三条数据通路的第三步都汇入此处。</summary>
        private readonly List<DataRecord> _pendingBatch = new();  // 缓冲队列：三步汇合点的第三步往这里塞
        /// <summary>保护 <see cref="_pendingBatch"/> 的同步锁；锁内只做加入/取出+清空，写库在锁外。</summary>
        private readonly object _batchLock = new();               // 保护 _pendingBatch（MQTT 线程与心跳线程都会写）
        /// <summary>串行化 SQLite 批量写入的异步信号量（1,1），防止多批数据并发写冲突。</summary>
        private readonly SemaphoreSlim _flushLock = new(1, 1);    // 异步写库互斥：防止两批数据同时写 SQLite
        /// <summary>WAL 检查点的固定周期：60 秒。</summary>
        private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(60); // WAL 回收周期
        /// <summary>自动落库触发条数，来自 config.json；非正值时回退默认 50。</summary>
        private int _batchSize;                                   // 自动落库触发条数（来自 config.json，默认 50）
        /// <summary>上次执行 WAL 检查点的 UTC 时间。</summary>
        private DateTime _lastCheckpointUtc = DateTime.UtcNow;    // 上次 WAL 检查点时间
        /// <summary>对象释放标记：置位后各数据通路与冲刷路径静默短路。</summary>
        private volatile bool _disposed = false;                  // 释放标记：Dispose 后各路径静默短路

        /// <summary>每指标在内存中保留的最大历史点数（60）。</summary>
        public int MaxHistoryPoints { get; } = 60;

        /// <summary>未知节点计数：NODE_ID 不在路由表中（含 0xFF 保留值）而被丢弃的帧累计数。</summary>
        public int UnknownNodeCount { get; private set; }

        /// <summary>未匹配设备计数：路由表指向的设备名在当前设备列表中不存在而被丢弃的帧累计数。</summary>
        public int UnroutedDeviceCount { get; private set; }

        /// <summary>获取当前数据源显示名（枚举经 ToDisplay 映射，UI 与 config.json 均消费显示名）。</summary>
        public string DataSource => _selectedDataSource.ToDisplay();

        /// <summary>当前数据源运行状态（正常/故障/不可用）；与用户选择无关，故障不会改写选择。</summary>
        public DataSourceState State => _state;

        /// <summary>最近一次数据源故障原因；状态正常时为 null。</summary>
        public string? LastDataSourceError => _lastDataSourceError;

        /// <summary>当前串口链路健康状态（未选串口/未连接时为 Inactive）；只在跃迁时广播。</summary>
        public LinkHealth LinkHealth => _linkHealth;

        /// <summary>链路健康变更通知：仅在状态真正跃迁时触发（每秒心跳不重复广播同值）。</summary>
        public event EventHandler<LinkHealthChangedEventArgs>? LinkHealthChanged;

        /// <summary>数据源状态变更通知：每次切换完成（成功或失败）触发，携带状态与失败原因。</summary>
        public event EventHandler<DataSourceStateChangedEventArgs>? DataSourceStateChanged;

        /// <summary>写入数据源运行状态并广播（不改变用户所选数据源、不写配置文件）。</summary>
        /// <param name="state">新的运行状态。</param>
        /// <param name="reason">失败原因（面向用户）；正常时传 null。</param>
        private void SetState(DataSourceState state, string? reason)
        {
            _state = state;
            _lastDataSourceError = state == DataSourceState.Online ? null : reason;
            DataSourceStateChanged?.Invoke(this,
                new DataSourceStateChangedEventArgs(state, _selectedDataSource.ToDisplay(), _lastDataSourceError));
        }

        /// <summary>UI 数据源下拉框的可选项：由枚举自动生成（新增数据源只需扩枚举与映射表）。</summary>
        public string[] AvailableDataSources { get; } =
            Enum.GetValues<DataSourceKind>().Select(s => s.ToDisplay()).ToArray(); // UI 下拉框选项来源

        // ===== 构造函数族 =====
        // 构造函数族收敛说明：
        //   之前的 1参/2参×2/3参/5参 靠 : this(...) 链式调用，且 1参内部 new ConfigService()，
        //   导致 ConfigService 无法从 DI 注入（会被链上的 new 覆盖）。
        //   现收敛为两个构造函数：
        //     ① public 6 参（DI 选中）：全功能，所有依赖必填，从 DI 注入 ConfigService（单例）
        //     ② internal 测试专用：ConfigService 与 MqttService 可选（不对 DI 暴露，不影响构造函数选择）
        //   公共初始化抽入 Initialize(ConfigService)，两个构造函数都调用。

        /// <summary>
        /// 全功能构造函数（DI 运行时选中）：报警 + MQTT 通路 + 批量落库 + 串口通路全部注入，
        /// 配置服务由 DI 单例注入，构造时完成公共初始化并挂接所有事件。
        /// </summary>
        /// <param name="alarmService">报警判定与边沿通知服务。</param>
        /// <param name="mqttService">MQTT 数据源服务。</param>
        /// <param name="databaseService">数据库落库服务。</param>
        /// <param name="serialService">串口数据源服务（原始字节来源）。</param>
        /// <param name="frameParser">协议帧解析器（原始字节 → SensorDataFrame）。</param>
        /// <param name="configService">配置服务（DI 单例，全局唯一副本）。</param>
        public DeviceService(IAlarmService alarmService, IMqttService mqttService, IDatabaseService databaseService,
            ISerialService serialService, FrameParser frameParser, ConfigService configService)
        {
            _alarmService = alarmService;
            _mqttService = mqttService;
            _databaseService = databaseService;
            _serialService = serialService;
            _frameParser = frameParser;
            _configService = configService;
            _devices = new List<Device>();
            InitializeBatchSettings();
            // 路由表为启动只读快照（运行中不变），构造时建立路由器即可
            _frameRouter = new FrameRouter(_configService.GetNodeRoutingSnapshot());
            // MQTT 接线：消息到达自动流入 OnMqttMessageReceived（数据通路②的源头）
            _mqttService.MessageReceived += OnMqttMessageReceived;
            _mqttService.Disconnected += OnMqttDisconnected;
            // 串口接线：原始字节 → OnSerialRawData → FrameParser → OnFrameParsed（数据通路③的源头）
            _serialService.DataReceived += OnSerialRawData;
            _serialService.ConnectionStateChanged += OnSerialConnectionStateChanged;
            _frameParser.FrameParsed += OnFrameParsed;
        }

        // 单元测试专用：internal 对 DI 容器不可见，不参与构造函数选择。
        // 与 public 版的关键差异：ConfigService 由测试提供（可指向临时目录），
        // MqttService 可选——需要测试 MQTT 通路时传入，否则留空。
        /// <summary>
        /// 单元测试专用构造函数：由测试显式提供配置实例（可指向临时目录），MQTT 通路可选。
        /// </summary>
        /// <param name="alarmService">报警判定与边沿通知服务（可为测试替身）。</param>
        /// <param name="databaseService">数据库落库服务（可为测试替身）。</param>
        /// <param name="configService">测试提供的配置服务实例；为 null 时自动新建（读取真实 config.json）。</param>
        /// <param name="mqttService">可选 MQTT 服务；传入时挂接消息与断线事件，用于测试 MQTT 通路。</param>
        /// <remarks>声明为 internal 并对 DI 容器不可见，不参与构造函数选择。</remarks>
        internal DeviceService(IAlarmService alarmService, IDatabaseService databaseService,
            ConfigService? configService = null, IMqttService? mqttService = null)
        {
            _alarmService = alarmService;
            _databaseService = databaseService;
            _configService = configService ?? new ConfigService();
            _devices = new List<Device>();
            InitializeBatchSettings();
            if (mqttService != null)
            {
                _mqttService = mqttService;
                _mqttService.MessageReceived += OnMqttMessageReceived;
                _mqttService.Disconnected += OnMqttDisconnected;
            }
        }

        /// <summary>
        /// 从配置服务初始化批量落库参数：批量条数（非正值回退 50）与持久化的数据源选择。
        /// </summary>
        private void InitializeBatchSettings()
        {
            _batchSize = _configService.BatchSize > 0 ? _configService.BatchSize : 50;
            // config.json 中的历史值可能是任意字符串（旧版本/手工编辑），未知值回退模拟数据
            _selectedDataSource = _configService.DataSource.TryToDataSource(out var persisted)
                ? persisted : DataSourceKind.Simulation;
        }

        /// <summary>
        /// 启动时按需预连接数据源：持久化为串口则尝试连接串口；
        /// 持久化为 MQTT 时连接 Broker 并订阅 metalmonitor/devices/# 主题。
        /// </summary>
        /// <returns>表示异步初始化操作的任务。</returns>
        /// <remarks>
        /// 非串口/非 MQTT 数据源直接返回，避免抢占用户选择的数据源；
        /// 连接或订阅失败时保持用户所选数据源不变，仅标记 <see cref="DataSourceState.Faulted"/> 并广播原因。
        /// </remarks>
        public async Task InitializeDataSourceAsync()
        {
            // 启动时若持久化数据源为串口：复用切源逻辑尝试连接（失败时同样只标记状态，不改配置）
            if (_selectedDataSource == DataSourceKind.Serial)
            {
                await SetDataSourceAsync(DataSourceKind.Serial.ToDisplay()).ConfigureAwait(false);
                return;
            }

            // 仅当保存的数据来源为 MQTT 时才在启动时预连接，避免抢占用户选择的数据源
            if (_selectedDataSource != DataSourceKind.Mqtt)
                return;

            if (_mqttService != null && !_mqttService.IsConnected)
            {
                try
                {
                    await _mqttService.ConnectAsync().ConfigureAwait(false);
                    await _mqttService.SubscribeAsync("metalmonitor/devices/#").ConfigureAwait(false);
                    SetState(DataSourceState.Online, null);
                }
                catch (Exception ex)
                {
                    // 连接失败：保持用户所选 MQTT 不变，只标记故障并广播；不回退模拟数据、不写配置
                    SetState(DataSourceState.Faulted, ex.Message);
                }
            }
        }

        /// <summary>
        /// 切换数据来源：先强制冲刷缓冲防丢数据，再持久化选择并按目标通路连接/断开 MQTT。
        /// </summary>
        /// <param name="source">目标数据源名称（"模拟数据"/"MQTT"/"串口"）。</param>
        /// <returns>表示异步切换操作的任务。</returns>
        /// <remarks>
        /// 用户所选数据源先持久化，再按目标通路执行连接/断开；
        /// 连接失败或能力缺失时不改写用户选择与 config.json，只置 <see cref="State"/> 并广播原因；
        /// 切回模拟数据时若 MQTT/串口已连接则主动断开。冲刷与断连异常被隔离吞掉，不阻断切换。
        /// </remarks>
        public async Task SetDataSourceAsync(string source)
        {
            CancelReconnect();   // 切源前先取消后台重连循环，避免"已切走还在偷偷连串口"

            // 切换数据源前先冲刷剩余的缓冲数据，避免切换瞬间丢失
            try { await FlushAsync().ConfigureAwait(false); } catch { }

            // UI/配置传入的是显示名；未知名称（历史 config.json / 手工编辑）统一回退模拟数据
            if (!source.TryToDataSource(out var target))
                target = DataSourceKind.Simulation;

            _selectedDataSource = target;
            _configService.DataSource = target.ToDisplay();
            switch (target)
            {
                case DataSourceKind.Mqtt:
                    if (_mqttService != null)
                    {
                        try
                        {
                            if (!_mqttService.IsConnected)
                            {
                                await _mqttService.ConnectAsync().ConfigureAwait(false);
                                await _mqttService.SubscribeAsync("metalmonitor/devices/#").ConfigureAwait(false);
                            }
                            SetState(DataSourceState.Online, null);
                        }
                        catch (Exception ex)
                        {
                            // 连接失败：保持用户选择不变，只标记故障并广播（不再回退模拟数据）
                            SetState(DataSourceState.Faulted, ex.Message);
                        }
                    }
                    else
                    {
                        SetState(DataSourceState.Unavailable, "MQTT 服务未注册");
                    }
                    // 串口启用中则断开，避免切到 MQTT 后旧串口通路继续推数据
                    if (_serialService?.IsConnected == true)
                    {
                        try { await _serialService.DisconnectAsync().ConfigureAwait(false); } catch { }
                    }
                    break;
                case DataSourceKind.Serial:
                    // 切到串口前先停掉 MQTT 通路（与模拟数据分支保持一致）
                    if (_mqttService != null && _mqttService.IsConnected)
                    {
                        try { await _mqttService.DisconnectAsync().ConfigureAwait(false); } catch { }
                    }

                    if (_serialService == null)
                    {
                        // 能力缺失：串口服务未注册（仅测试构造可能出现），标记不可用并明示原因
                        SetState(DataSourceState.Unavailable, "串口服务未注册");
                    }
                    else if (!_serialService.IsAvailable)
                    {
                        // 当前系统没有任何串口硬件：标记不可用；之后若插入设备，
                        // 心跳中的 TryStartReconnect 会依据"端口重现"判据自动拉起连接
                        SetState(DataSourceState.Unavailable, "当前系统未检测到任何串口设备");
                    }
                    else
                    {
                        // 按候选序列（持久化端口 → 历史成功端口 → 单端口兜底）逐个尝试；
                        // 只有"连上并在看门狗窗口内收到有效帧"才算成功——避免连错设备还报正常
                        bool connected = false;
                        string? error = null;
                        foreach (var portName in GetCandidatePorts())
                        {
                            var result = await TryConnectVerifiedAsync(portName, CancellationToken.None).ConfigureAwait(false);
                            if (result.Ok) { connected = true; break; }
                            error = result.Error;
                        }

                        if (!connected)
                            SetState(DataSourceState.Faulted, error ?? "串口连接失败：未能在任何候选端口上收到有效数据帧");
                    }
                    break;
                case DataSourceKind.Simulation:
                    if (_mqttService != null && _mqttService.IsConnected)
                    {
                        try { await _mqttService.DisconnectAsync().ConfigureAwait(false); } catch { }
                    }
                    // 串口启用中则断开，避免切到模拟数据后旧串口通路继续推数据
                    if (_serialService?.IsConnected == true)
                    {
                        try { await _serialService.DisconnectAsync().ConfigureAwait(false); } catch { }
                    }
                    SetState(DataSourceState.Online, null);
                    break;
            }
        }

        // 重新加载指标配置：先冲刷缓冲数据（防止切换配置时丢数据），
        // 再从数据库读全量配置，同时把阈值同步给报警服务（报警判定依赖这些阈值）
        /// <summary>
        /// 重新从数据库加载全量指标配置，并把阈值字典同步给报警服务。
        /// </summary>
        /// <returns>表示异步重载操作的任务；无数据库服务时仅冲刷缓冲后返回。</returns>
        /// <remarks>
        /// 加载前先强制冲刷缓冲，防止配置切换导致在途数据丢失；
        /// 配置快照以整体替换方式更新，全程 ConfigureAwait(false)，Core 层不依赖 UI 同步上下文。
        /// </remarks>
        public async Task ReloadMetricConfigsAsync()
        {
            try { await FlushAsync().ConfigureAwait(false); } catch { }

            if (_databaseService != null)
            {
                // ConfigureAwait(false)：Core 层不碰 UI，无需回原始同步上下文，
                // 也避免 Dispose 时 GetAwaiter().GetResult() 与 UI 线程形成死锁
                _metricConfigs = await _databaseService.GetAllMetricConfigsAsync().ConfigureAwait(false);
                var dict = new Dictionary<string, MetricConfig>();
                foreach (var c in _metricConfigs)
                    dict[c.Name] = c;
                // D3：同一份字典既供报警阈值，也供单位查询——热路径不再每次线性扫 _metricConfigs
                _metricConfigsByName = dict;
                _alarmService.UpdateThresholds(dict);
            }
        }

        /// <summary>
        /// 获取当前内存中的指标配置快照引用。
        /// </summary>
        /// <returns>指标配置列表；该列表为整体替换式更新，调用方不应就地修改其内容。</returns>
        public List<MetricConfig> GetMetricConfigs()
        {
            return _metricConfigs;
        }

        // MQTT 通路入口：回调来自 MQTTnet 内部线程（非 UI 线程）；
        // 坏消息解析失败静默丢弃（故障隔离：一条坏消息不能中断后续消息处理）
        /// <summary>
        /// MQTT 消息接收入口（数据通路②）：反序列化消息负载并转交设备更新逻辑。
        /// </summary>
        /// <param name="sender">事件发送方（MqttService 实例），未使用。</param>
        /// <param name="e">消息事件参数，Payload 为设备指标 JSON。</param>
        /// <remarks>
        /// 运行在 MQTTnet 后台线程；反序列化失败或负载为 null 时静默丢弃，
        /// 做异常隔离以保证单条坏消息不中断后续消息处理。
        /// </remarks>
        private void OnMqttMessageReceived(object? sender, MqttMessageReceivedEventArgs e)
        {
            try
            {
                var deviceData = JsonSerializer.Deserialize<MqttDeviceData>(e.Payload);
                if (deviceData != null)
                {
                    UpdateDeviceFromMqtt(deviceData);
                }
            }
            catch
            {
            }
        }

        // MQTT 消息 → 设备指标的映射，与模拟数据走同一个"三步汇合点"
        /// <summary>
        /// 将一条 MQTT 设备消息映射到设备指标，逐指标执行与模拟通路一致的三步：
        /// 报警判断 → SetMetricValue 触发 UI 事件 → 进落库缓冲。
        /// </summary>
        /// <param name="deviceData">反序列化后的设备消息（设备名 + 指标键值集）。</param>
        /// <remarks>
        /// 设备查找在 <see cref="_devicesLock"/> 内完成，取出对象后的操作在锁外进行
        /// （Device 内部使用线程安全结构，避免长时间持锁）；
        /// 设备不存在、负载缺指标集或设备被停用（IsActive=false）时直接忽略，不更新/不报警/不落库；
        /// 先写报警状态再写值，避免 UI 读到"新值+旧报警状态"的瞬时不一致。
        /// </remarks>
        private void UpdateDeviceFromMqtt(MqttDeviceData deviceData)
        {
            Device? device;
            // lock 保护设备集合的并发读：MQTT 后台线程查设备，
            // 同时 UI 线程可能在 ReloadDevices 修改集合；取出 device 后在锁外操作该对象
            // （Device 内部用 ConcurrentDictionary 已线程安全，避免长时间持锁）
            lock (_devicesLock)
            {
                // D3：改用按名索引（语义与旧 FirstOrDefault 等价：同名取先出现者，见 RebuildDeviceIndex）
                _devicesByName.TryGetValue(deviceData.Name, out device);
            }
            if (device == null || deviceData.Metrics == null)
                return;

            // 与模拟数据通路对齐：用户在 UI 禁用的设备，即使 MQTT 推来数据也不更新/不报警/不落库
            if (!device.IsActive)
                return;

            foreach (var kvp in deviceData.Metrics)
            {
                string metricName = kvp.Key;
                double value = kvp.Value;

                // 接入层汇合点：只写暂存区 + 追加落库；报警判定与写值统一在心跳排空时执行，
                // 全项目只保留一套"业务三步"实现（见 ApplyPendingSamples）
                StageSample(device, metricName, value);
            }
        }

        // 串口通路入口·第一段：SerialPort 后台线程推来原始字节，转交 FrameParser 组装成帧；
        // 帧组装与 CRC 校验不在本类，本类只"喂字节"
        /// <summary>
        /// 串口原始字节接收入口（数据通路③的第一段）：把本次收到的原始字节转交 <see cref="FrameParser"/> 喂入，
        /// 由解析器组装成完整帧后再经 <see cref="OnFrameParsed"/> 处理。
        /// </summary>
        /// <param name="sender">事件发送方（SerialService 实例），未使用。</param>
        /// <param name="e">原始字节事件参数，载荷为本次读取的原始字节（可能半帧或粘包）。</param>
        /// <remarks>运行在 SerialPort 内部后台线程；整体 try-catch，避免异常被转寄到全局异常处理器。</remarks>
        private void OnSerialRawData(object? sender, SerialRawDataEventArgs e)
        {
            try
            {
                // 先记"任意字节"时刻（乱码也算链路有信号），再喂解析器
                long now = Environment.TickCount64;
                Volatile.Write(ref _lastSerialByteMs, now);
                _frameParser?.Feed(e.Data);
            }
            catch
            {
                // 回调事件处理器不得把异常抛回后台线程，静默吞掉
            }
        }

        // 串口通路入口·第二段：解析后的帧按 NODE_ID 路由到设备，命中后写暂存区 + 追加落库；
        // 未知节点/未匹配设备两类帧丢弃并各自计数（路由是策略，判定在 FrameRouter）
        /// <summary>
        /// 串口协议帧接收入口（数据通路③的第二段）：按 NODE_ID 路由到设备，命中后把温度/电压样本写入暂存区
        /// 并追加落库缓冲；未知节点与未匹配设备两类帧丢弃并各自计数。
        /// </summary>
        /// <param name="sender">事件发送方（FrameParser 实例），未使用。</param>
        /// <param name="frame">解析后的传感器数据帧（NODE_ID/温度/电压/DS18B20 状态）。</param>
        /// <remarks>
        /// 运行在串口后台线程；串口能力缺失（3/4 参构造，<c>_frameRouter</c> 为 null）时安全跳过；
        /// 设备被停用（IsActive=false）时跳过，不更新/不报警/不落库；
        /// 指标键名与 MQTT 通路完全一致：Temperature（℃ 原值）、Voltage（由帧内 mV 换算为 V）；
        /// 整体 try-catch，避免异常被转寄到全局异常处理器。
        /// </remarks>
        private void OnFrameParsed(object? sender, SensorDataFrame frame)
        {
            try
            {
                // 有效帧时刻先于路由判定记录：未知节点/未匹配设备的帧同样证明"链路能解析出真帧"
                Volatile.Write(ref _lastSerialValidFrameMs, Environment.TickCount64);

                // 串口能力缺失（3/4 参构造）时 _frameRouter 为 null，安全跳过
                if (_frameRouter == null) return;

                // 设备列表取快照后在锁外判定：FrameRouter 不持锁、无副作用
                List<Device> snapshot;
                lock (_devicesLock)
                {
                    snapshot = _devices.AsReadOnly().ToList();
                }

                var outcome = _frameRouter.Resolve(frame.NodeId, snapshot, out var device);
                switch (outcome)
                {
                    case RouteOutcome.UnknownNode:
                        // NODE_ID 不在路由表中（含 0xFF 保留值）：丢弃并计数
                        UnknownNodeCount++;
                        Console.WriteLine($"[DeviceService] 丢弃未知节点帧 NODE_ID=0x{frame.NodeId:X2}");
                        return;
                    case RouteOutcome.UnroutedDevice:
                        // 路由表指向的设备名在当前设备列表中不存在：丢弃并计数
                        UnroutedDeviceCount++;
                        Console.WriteLine($"[DeviceService] 丢弃未匹配设备帧 NODE_ID=0x{frame.NodeId:X2}");
                        return;
                }

                if (device == null) return;

                // 用户禁用设备：不更新/不报警/不落库
                if (!device.IsActive) return;

                // 指标键名与 MQTT 通路保持一致；电压由帧内的 mV 换算为 V（MetricConfig 的 Voltage 单位为 V）
                StageSample(device, "Temperature", frame.Temperature);
                StageSample(device, "Voltage", frame.VoltageMv / 1000.0);
            }
            catch
            {
                // 回调事件处理器不得把异常抛回后台线程，静默吞掉
            }
        }

        /// <summary>
        /// 串口连接状态回调：维护连接时刻并按新连接重新开始判据（连接/断开都清零字节与有效帧时间戳）。
        /// </summary>
        /// <remarks>运行在串口后台线程；整体 try-catch，异常不得抛回后台线程。</remarks>
        private void OnSerialConnectionStateChanged(object? sender, bool connected)
        {
            try
            {
                long now = Environment.TickCount64;
                Volatile.Write(ref _serialConnectedAtMs, connected ? now : 0);
                Volatile.Write(ref _lastSerialByteMs, 0);
                Volatile.Write(ref _lastSerialValidFrameMs, 0);
                EvaluateLinkHealthAt(now);
            }
            catch
            {
                // 回调事件处理器不得把异常抛回后台线程，静默吞掉
            }
        }

        /// <summary>
        /// 按当前时间戳评估链路健康；仅在状态跃迁时赋值并广播。
        /// </summary>
        /// <param name="nowMs">评估时刻（单调毫秒）；单测可传假时间，无需等待真实秒数。</param>
        internal void EvaluateLinkHealthAt(long nowMs)
        {
            if (_disposed) return;

            int watchdogSeconds = _configService.SerialWatchdogSeconds;
            bool reconnecting = Interlocked.CompareExchange(ref _reconnectRunningFlag, 0, 0) == 1;  // 原子读
            var next = LinkHealthEvaluator.Evaluate(
                nowMs,
                Volatile.Read(ref _serialConnectedAtMs),
                Volatile.Read(ref _lastSerialByteMs),
                Volatile.Read(ref _lastSerialValidFrameMs),
                watchdogSeconds,
                reconnecting);

            lock (_linkHealthLock)
            {
                if (next == _linkHealth) return;   // 同值不广播，避免每秒心跳造成事件风暴
                _linkHealth = next;
            }

            // 事件在锁外触发，订阅方回调不延长持锁时间
            LinkHealthChanged?.Invoke(this, new LinkHealthChangedEventArgs(
                next, DataSourceKind.Serial.ToDisplay(), BuildLinkHealthReason(next, watchdogSeconds)));
        }

        /// <summary>
        /// 给链路健康状态生成面向用户的一句话原因（健康时为 null）。
        /// </summary>
        private static string? BuildLinkHealthReason(LinkHealth health, int watchdogSeconds) => health switch
        {
            LinkHealth.Inactive => "串口未连接",
            LinkHealth.Recovering => "串口已连接，等待首帧",
            LinkHealth.NoSignal => $"超过 {watchdogSeconds} 秒未收到任何串口数据",
            LinkHealth.NoValidFrame => $"超过 {watchdogSeconds} 秒未收到有效数据帧",
            LinkHealth.Reconnecting => "链路已断开，正在后台自动重连",
            _ => null
        };

        /// <summary>
        /// 由心跳每秒调用一次：判定是否需要启动重连，以及是否需要唤醒正在退避等待的重连循环。
        /// </summary>
        /// <remarks>
        /// 按顺序做四个判断，任何一步不满足就返回、不做多余动作：
        ///   1. 现在选的是不是串口？不是就不归它管（另一条通路自己带重连机制）。
        ///   2. 重连循环是否已在运行；若已运行则不重复启动，仅在记忆端口重新出现时唤醒该循环。
        ///      为什么强调"刚来"而不是"一直在"：见 _lastSeenSerialPorts 的注释。
        ///   3. 没人值班时，判断该不该派，两条理由任一成立就派：
        ///      · 数据已经断了（3 秒没收到任何字节）→ 理由"通讯中断"。
        ///      · 压根没连上，但目标设备已经插在电脑上 → 理由"检测到设备接入"。
        ///      第二条的"压根没连上"是护栏：正常连着时这条永远不成立，否则等于每秒把自己重连一遍。
        ///   4. 该派 → 原子地占住"正在值班"这个位置 → 顶栏先显示"连接失败 / 自动重连中" → 后台开工。
        /// 为什么"断了"不用 IsConnected 判断？因为拔掉线之后它仍然说"连着"（对象自己不知道线没了），
        /// 只有"多久没收到数据"这一条是可信的。
        /// </remarks>
        private void TryStartReconnect()
        {
            if (_selectedDataSource != DataSourceKind.Serial) return;

            // 循环已在运行：不重复启动；仅当记忆端口刚刚出现时唤醒，使其立即重试而无需等待完整退避。
            // 采用"刚刚出现"而非"一直在场"：若设备持续在场但无法通信，每拍唤醒会使退避机制失效。
            if (_reconnectRunningFlag == 1)
            {
                if (SerialPortNewlyAppeared())
                    WakeReconnectLoop();
                return;
            }

            // 循环没在跑：先把当前端口记成"基线"，免得刚启动后的第一拍把本来就在场的设备误判成"刚来的"
            _lastSeenSerialPorts = GetCurrentSerialPorts();

            string? reason;
            // 理由一：3 秒没收到任何字节 —— 这是"线断了"唯一可信的证据（拔线时系统仍说"连着"）
            if (_linkHealth == LinkHealth.NoSignal)
                reason = "通讯中断，自动重连中";
            // 理由二：还没连上，但电脑上存在串口 —— 启动时没接线 / 掉线后插回了另一个口。
            // 判据为"存在任意串口"：启动后从未连上（Inactive）且记忆端口全部不在场时，
            // 只要电脑上还有串口，就当作设备可能插在了没记过的口上，交给 GetCandidatePorts 逐个试。
            else if (_serialService?.IsConnected != true
                     && (SerialPortAppeared() || GetCurrentSerialPorts().Length > 0))
                reason = "检测到串口设备接入，自动重连中";
            else
                return;

            // 抢位子：CAS 只有第一个人能把 0 换成 1，后面的都看见 1 直接放弃（避免两个心跳派两个人）
            if (Interlocked.CompareExchange(ref _reconnectRunningFlag, 1, 0) != 0) return;   // 是1时return
            if (_disposed) { Interlocked.Exchange(ref _reconnectRunningFlag, 0); return; }   // Dispose 窗口竞争

            // 断线与待接入均属故障恢复过程：顶栏第一行显示"连接失败"，与第二行"重连中"共同表达当前状态
            SetState(DataSourceState.Faulted, reason);

            // 先做一次主动 DisconnectAsync 把旧句柄清干净（IgnoreAwaitfire-and-forget）
            _ = Task.Run(async () =>
            {
                if (_serialService != null)
                {
                    try { await _serialService.DisconnectAsync().ConfigureAwait(false); } catch { }
                }

                // 异步启动循环，自身 catch 一切异常不得把程序打挂
                try
                {
                    _reconnectAttempt = 0;
                    _reconnectCts = new CancellationTokenSource();
                    await ReconnectLoopAsync(_reconnectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* 正常取消，静默退出 */ }
                catch
                {
                    // 循环内一切非取消异常静默吞掉：不得让后台任务打挂进程
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectRunningFlag, 0);
                    _reconnectCts?.Dispose();
                    _reconnectCts = null;
                }
            });
        }

        /// <summary>
        /// 重连主循环：按退避间隔等待，每次唤醒后尝试连接，失败则延长间隔，直至连接成功、被取消或对象释放。
        /// </summary>
        /// <param name="ct">真正的收工信号（切源、Dispose、手动取消时置位）。</param>
        /// <remarks>
        /// 每一轮的四个动作：
        ///   1. 依据当前尝试次数计算等待时长（1 秒起，逐次翻倍，上限 30 秒，附加随机抖动以避免同步重试）
        ///   2. 执行等待；该等待可被外部唤醒提前中断（设备重新插入时触发）
        ///   3. 醒来后按"敲门顺序"挨个试门，每个门都要听 3 秒才下结论
        ///   4. 任何一个门通过就收工；全都没通过就进入下一轮，睡得更久一点
        /// 为什么无限重试：现场可能半夜才恢复供电，程序放弃就等于要人守夜。
        /// </remarks>
        private async Task ReconnectLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                int baseSeconds = _configService.SerialReconnectBaseSeconds;
                int maxSeconds = Math.Max(baseSeconds, _configService.SerialReconnectMaxSeconds);   // 上限小于基准时自动对齐

                int delayMs = ReconnectBackoff.GetDelayMs(_reconnectAttempt, baseSeconds, maxSeconds);
                _reconnectAttempt++;   // 先递增（下次尝试序号），然后等

                // 本次等待可被提前中断：外部唤醒会取消 wakeCts，等待立即结束并继续执行连接尝试；
                // 终止信号（切换数据源/关闭程序）使用 ct；两者分离，避免将唤醒误判为终止。
                using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _reconnectWakeCts = wakeCts;   // 暴露给外部，供唤醒逻辑取消
                try
                {
                    await Task.Delay(delayMs, wakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 醒来的原因有两种，必须分开处理：
                    //   ct 被取消 = 真收工 → 直接回家，不再尝试
                    //   仅 wakeCts 被取消表示外部唤醒：重置退避计数，立即重试
                    if (ct.IsCancellationRequested) return;   // 真取消：切源/Dispose/手动取消
                    _reconnectAttempt = 0;                    // 外部唤醒：重置退避计数
                }
                finally
                {
                    // 只拆自己挂的那把开关：下一轮会挂新的，别把新的误拆了
                    if (ReferenceEquals(_reconnectWakeCts, wakeCts))
                        _reconnectWakeCts = null;
                }

                if (ct.IsCancellationRequested || _disposed) return;
                if (_selectedDataSource != DataSourceKind.Serial) return;   // 用户可能在等待期间切了源

                if (_serialService == null || !_serialService.IsAvailable) continue;

                // 按候选序列逐个尝试，任一端口通过验证即成功（可兼容端口漂移）
                foreach (var portName in GetCandidatePorts())
                {
                    var result = await TryConnectVerifiedAsync(portName, ct).ConfigureAwait(false);
                    if (result.Ok) return;   // 成功：状态已置 Online、端口已持久化；RunningFlag 由 finally 归零
                    if (ct.IsCancellationRequested || _disposed) return;
                }
            }
        }

        /// <summary>
        /// 取消重连循环（切源、Dispose 时调用）：立即置位 CTS，让循环退出 RunningFlag 归零。
        /// 幂等：CTS 可能为 null 或已取消，全部安全处理。
        /// </summary>
        private void CancelReconnect()
        {
            _reconnectCts?.Cancel();
        }

        /// <summary>
        /// 现在电脑上有哪些串口。服务缺失或没硬件时给空名单，而不是 null——
        /// 这样调用方不用到处写"先判空再判长度"，读起来不会一堆 if 套 if。
        /// </summary>
        private string[] GetCurrentSerialPorts() =>
            _serialService?.IsAvailable == true ? _serialService.GetAvailablePorts() : Array.Empty<string>();

        /// <summary>
        /// 返回记忆端口列表：先取上次成功的端口，再依次追加历史成功端口，并去除重复项。
        /// 为什么上次成功的排最前：绝大多数情况它就是对的那个，试它一次就成，不用挨个试到最后一个。
        /// 去重原因：两个来源（上次成功端口与历史端口列表）可能记录同一端口，重复尝试会浪费一轮验证窗口。
        /// </summary>
        private List<string> GetRememberedPorts()
        {
            var remembered = new List<string>();
            void AddIfAbsent(string? port)
            {
                if (!string.IsNullOrWhiteSpace(port)
                    && !remembered.Contains(port, StringComparer.OrdinalIgnoreCase))
                    remembered.Add(port);
            }

            AddIfAbsent(_configService.SerialPortName);
            foreach (var port in _configService.SerialKnownPorts)
                AddIfAbsent(port);
            return remembered;
        }

        /// <summary>
        /// "目标设备现在是不是插在电脑上"——注意问的是"在不在"，不管它是刚插的还是插了很久。
        /// 用途：决定"根本还没连上时，要不要因为看见设备而主动去连一次"。
        /// </summary>
        /// <remarks>
        /// 判断规则分两种情形：
        ///   · 记忆端口列表为空（首次使用或更换电脑）时，只要系统存在任意串口即判定为可能在线。
        ///   · 记忆端口列表非空时，其中任一端口在场即判定为可能在线。
        ///     这样换口后名字变了（比如从三号变成四号），只要四号也曾经成功过，也能被发现。
        /// 与 SerialPortNewlyAppeared 的区别：这个只回答"在不在"，那个回答"是不是刚来的"。
        /// </remarks>
        private bool SerialPortAppeared()
        {
            string[] ports = GetCurrentSerialPorts();
            if (ports.Length == 0) return false;

            var remembered = GetRememberedPorts();
            if (remembered.Count == 0) return true;   // 毫无记忆（首次运行/换机）：任何端口出现都算

            // 本子上任意一个名字在场就算：换口后名字变了，只要新名字在本子上也能发现
            return ports.Any(p => remembered.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// "是不是刚来了一个本子上记着的设备"——只有本次比上一拍多出来的端口才算（边沿判断）。
        /// 用途：唤醒正在运行的重连循环，使其立即重试一次。
        /// </summary>
        /// <remarks>
        /// 为什么必须"只认刚来的"：假设插着一个能打开、但收不到数据的设备（比如波特率不对），
        /// 若以"在场"为唤醒条件，设备持续插着时每拍都会唤醒等待中的循环，
        /// "失败后越等越久"的机制就完全失效，变成每 3 秒死磕一次。
        /// 注意：调用一次就把基线更新成当前名单，所以同一个"新人"只会报一次。
        /// </remarks>
        private bool SerialPortNewlyAppeared()
        {
            string[] ports = GetCurrentSerialPorts();
            // 本次有、上次没有 = 新来的
            var appeared = ports
                .Where(p => !_lastSeenSerialPorts.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            _lastSeenSerialPorts = ports;   // 基线前移：同一个"新人"只报一次

            if (appeared.Length == 0) return false; //没有新的

            var remembered = GetRememberedPorts();
            if (remembered.Count == 0) return true;   // 无记忆：新出现任何端口都算
            return appeared.Any(p => remembered.Contains(p, StringComparer.OrdinalIgnoreCase)); //多出来的这个端口已被记录->true
        }

        /// <summary>
        /// 唤醒重连循环：取消当前等待，使循环立即继续执行一次连接尝试。
        /// 循环没在睡觉时调用它什么也不会发生，所以调用方不用先判断"它睡没睡"。
        /// </summary>
        private void WakeReconnectLoop()
        {
            try { _reconnectWakeCts?.Cancel(); }
            catch (ObjectDisposedException) { /* 正赶上循环收尾把开关拆了，忽略即可 */ }
        }

        /// <summary>
        /// 候选端口顺序：优先尝试记忆端口，其次按兜底规则枚举在场端口。
        /// </summary>
        /// <returns>按顺序该试的端口名；没有串口服务时给空名单。</returns>
        /// <remarks>
        /// 三层，顺序有讲究：
        ///   1. 上次成功的那个名字 —— 最可能一击命中，放第一。
        ///   2. 本子上其他成功过的名字 —— 换口后新名字通常在这里。
        ///   3. 兜底 —— 本子上的名字全都不在场时，才考虑"盲敲"：
        ///      · 本子整体为空（第一次用）→ 把所有在场的门都试一遍，别直接放弃；
        ///      · 只剩唯一一个门开着 → 试它。
        /// 为什么第 3 层不无条件"把所有门都试一遍"：门越多，每试一个都要等 3 秒验证，
        /// 会把一次恢复拖成十几秒；只有在完全没有线索时才值得这么做。
        /// </remarks>
        private List<string> GetCandidatePorts()
        {
            var candidates = new List<string>();
            if (_serialService == null) return candidates;

            string[] available = _serialService.GetAvailablePorts();

            // 第一层、第二层：上次成功的名字 → 本子上其他名字（GetRememberedPorts 已去重且保序）
            candidates.AddRange(GetRememberedPorts());

            if (candidates.Count == 0)
            {
                // 第三层之一：手里一个名字都没有（第一次用/换电脑），挨个试所有在场的门，而不是直接放弃。
                // 排序只是为了让每次尝试顺序一致、便于复现问题，不代表谁更优先。
                candidates.AddRange(available.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            else if (!candidates.Any(p => available.Contains(p, StringComparer.OrdinalIgnoreCase)) && available.Length == 1)
            {
                // 第三层之二：本子上的名字全都不在场，而电脑上刚好多出唯一一个门——试它
                candidates.Add(available[0]);
            }

            return candidates;
        }

        /// <summary>
        /// 试一个门：推开它，然后站在原地听 3 秒，看里面有没有人说话（收到真正的数据）。
        /// 有人说话才回来报"成功"，没人说话就关上门，让外面去试下一个。
        /// </summary>
        /// <remarks>
        /// 为什么要听 3 秒而不是"推开就报成功"：推开只证明这个门没锁，
        /// 门后面完全可能是别的设备（比如另一块板、一个别的串口工具），一条数据都不会来。
        /// 为什么听 3 秒这个数：它和看门狗用的是同一个值——"3 秒没数据就认为断了"，
        /// 反过来"3 秒内来了数据就认为通"，两个判断用同一把尺子，不会出现自相矛盾的标准。
        /// 为什么失败必须关上门：不关的话句柄一直被占着，下一个门可能因此打不开，看似"所有门都不行"。
        /// </remarks>
        /// <param name="portName">要试的门牌号。</param>
        /// <param name="ct">终止信号（切换数据源或关闭程序）；唤醒信号不传入，避免中断正在进行的验证窗口。</param>
        /// <returns>Ok = 是否通过倾听（通过时已经报"正常"并把门牌号写回配置）；Error = 推门时的异常信息（倾听没过则为 null）。</returns>
        private async Task<(bool Ok, string? Error)> TryConnectVerifiedAsync(string portName, CancellationToken ct)
        {
            // 记住"推门之前，最后一次听到声音是什么时候"，之后只看它有没有往后走
            long beforeFrameMs = Volatile.Read(ref _lastSerialValidFrameMs);
            try
            {
                await _serialService!.ConnectAsync(portName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);   // 端口不存在/被占用：试下一个候选
            }

            try
            {
                // 这就是"站在原地听 3 秒"：期间只要收到一帧通过校验的数据，下面的时间戳就会往后走
                await Task.Delay(_configService.SerialWatchdogSeconds * 1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 倾听过程中被要求收工（切源/关程序）：先把门关上再走，别把句柄漏在那儿
                try { await _serialService.DisconnectAsync().ConfigureAwait(false); } catch { }
                return (false, null);
            }

            if (Volatile.Read(ref _lastSerialValidFrameMs) > beforeFrameMs)
            {
                // 连接成功：更新上次成功端口与历史端口列表（在同一方法内完成，避免两处记录不一致）
                _configService.RecordSerialPortSuccess(portName);
                SetState(DataSourceState.Online, null);
                return (true, null);
            }

            // 3 秒内一声没听到：门后不是我们的设备（或者波特率不对），关上门去试下一个
            try { await _serialService.DisconnectAsync().ConfigureAwait(false); } catch { }
            return (false, null);
        }

        /// <summary>
        /// 按指标值年龄与链路状态执行质量降级扫描：写值路径已无条件置 Good，本方法只负责降级。
        /// </summary>
        /// <param name="nowMs">扫描时刻（单调毫秒）；单测可传假时间。</param>
        /// <remarks>
        /// 只扫描 <see cref="_metricConfigs"/> 中启用的指标（与 UI 展示口径一致）；
        /// 仅当所选数据源为串口且链路明确故障（Inactive/NoSignal/NoValidFrame）时才判 Bad，
        /// 其他情况超期只判 Stale——MQTT 运行中断线尚无事件，不能据此断言"值坏了"。
        /// </remarks>
        internal void ApplyQualityDegradationAt(long nowMs)
        {
            if (_disposed) return;

            bool linkFaulted = _selectedDataSource == DataSourceKind.Serial
                && _linkHealth != LinkHealth.Healthy
                && _linkHealth != LinkHealth.Recovering;

            int staleSeconds = _configService.MetricStaleSeconds;
            var metrics = _metricConfigs;   // 整体替换式更新，取引用快照即可

            List<Device> snapshot;
            lock (_devicesLock)
            {
                snapshot = _devices.AsReadOnly().ToList();
            }

            foreach (var device in snapshot)
            {
                if (!device.IsActive) continue;
                foreach (var metric in metrics)
                {
                    if (!metric.IsActive) continue;
                    var quality = MetricQualityEvaluator.Evaluate(
                        nowMs, device.GetMetricValueMs(metric.Name), staleSeconds, linkFaulted);
                    device.SetMetricQuality(metric.Name, quality);   // 同值内部短路，不重复通知
                }
            }
        }

        // 接入层汇合点：三条数据源（模拟/MQTT/串口）的接入段都调用这里——
        // 写线程安全暂存区（同键 latest-wins）+ 追加原始样本到落库缓冲（保真）
        /// <summary>
        /// 接入层汇合点：把一个"设备 + 指标"样本写入线程安全暂存区（同键只保留最新值），
        /// 并同步追加原始样本到落库缓冲。
        /// </summary>
        /// <param name="device">样本所属设备。</param>
        /// <param name="metricName">指标名（与 MetricConfig.Name 一致，如 Temperature/Voltage）。</param>
        /// <param name="value">指标值（已换算为物理量单位）。</param>
        /// <remarks>
        /// 暂存区保证同键 latest-wins（心跳排空时只派发最新值）；落库缓冲则逐条追加（保真：
        /// 即使同键被后到的样本覆盖，原始样本也已进缓冲）。报警判定与写值不在此执行——
        /// 统一交给心跳 <see cref="ApplyPendingSamples"/>，全项目只保留一套"业务三步"实现。
        /// </remarks>
        private void StageSample(Device device, string metricName, double value)
        {
            // 写暂存区：同一「设备+指标」只保留最新值（latest-wins）
            lock (_stagingLock)
                _pendingSamples[(device, metricName)] = value;

            // 追加原始样本到落库缓冲（与旧 MQTT/模拟路径一致：攒够 BatchSize 才批量写）
            if (_databaseService != null)
            {
                EnqueueDataRecord(new DataRecord
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    MetricName = metricName,
                    Value = value,
                    Unit = GetMetricUnit(metricName),
                    Timestamp = DateTime.Now
                });
            }
        }

        // 派发层排空：全项目唯一一套"业务三步"实现——
        // 报警判定 → SetMetricAlarmState → SetMetricValue → CheckAndNotify（边沿触发）
        /// <summary>
        /// 排空暂存区：对每一项执行「报警判定 → SetMetricAlarmState → SetMetricValue → CheckAndNotify」，
        /// 这是全项目唯一一套"业务三步"实现（模拟/MQTT/串口三条来源统一在此收口）。
        /// </summary>
        /// <remarks>
        /// 锁内仅"拷贝取出 + 清空"，逐项写设备在锁外执行（Device 内部线程安全，避免长时间持锁）；
        /// 报警状态先于指标值写入，保证 UI 事件回调读到的报警态与新值一致（见 Device 的注释约定）；
        /// 设备被停用（IsActive=false）时跳过，不更新/不报警；
        /// 同「设备+指标」经暂存区聚合后每拍只调用一次 CheckAndNotify，沿用其边沿触发语义。
        /// </remarks>
        private void ApplyPendingSamples()
        {
            List<KeyValuePair<(Device Device, string Metric), double>> samples;
            lock (_stagingLock)
            {
                if (_pendingSamples.Count == 0) return;
                samples = _pendingSamples.ToList();   // 拷贝取出
                _pendingSamples.Clear();              // 原暂存区清空，继续接新样本
            }

            foreach (var sample in samples)
            {
                var device = sample.Key.Device;
                string metricName = sample.Key.Metric;
                double value = sample.Value;

                if (!device.IsActive) continue;

                // 三步汇合点·第①步：报警判定（无状态只读）
                bool alarming = _alarmService.ShouldAlarm(metricName, value);

                // 三步汇合点·第②步：先写报警状态再写值——
                // 因为 SetMetricValue 会触发 UI 事件，UI 此时会同时读"值"和"是否报警"，
                // 若先写值后写状态，UI 可能读到"新值+旧报警状态"的不一致瞬间
                device.SetMetricAlarmState(metricName, alarming);
                device.SetMetricValue(metricName, value);   // 触发 PropertyChanged → UI 卡片刷新

                // 边沿检测：只在跳变时发事件；按「设备+指标」聚合后每拍仅一次
                _alarmService.CheckAndNotify(device.Name, metricName, value);
            }
        }

        // 模拟数据通路（心跳驱动，跑在 UI 线程）：
        // 每拍对每个活跃设备的每个活跃指标生成平滑随机值，走"报警→写值→缓冲"三步
        /// <summary>
        /// 模拟数据通路（数据通路①）：心跳驱动，遍历每个活跃设备的每个活跃指标，
        /// 生成平滑随机值并执行"报警判断 → SetMetricValue 触发 UI 事件 → 进落库缓冲"三步。
        /// </summary>
        /// <remarks>
        /// 运行在 UI 线程。指标配置仅取引用快照（整体替换式更新，迭代期间安全）；
        /// 设备列表则在 <see cref="_devicesLock"/> 内拷贝一份快照后遍历，
        /// 防止遍历期间集合被增删抛出"集合已修改"异常；
        /// 停用设备整台跳过，停用指标仅清除其报警状态；
        /// 报警状态先于指标值写入，保证 UI 事件回调读到的报警态与新值一致。
        /// </remarks>
        private void UpdateDeviceData()
        {
            // 快照引用而非拷贝内容：_metricConfigs 是整体替换式更新，
            // 只需在循环开始时拿一份引用，循环中即使被替换也不影响本次迭代
            var metrics = _metricConfigs;
            // 设备列表则需要真拷贝一份快照：因为循环过程中可能被增删，
            // 直接 foreach 原集合会抛"集合已修改"异常
            List<Device> snapshot;
            lock (_devicesLock)
            {
                snapshot = _devices.AsReadOnly().ToList();
            }
            foreach (var device in snapshot)
            {
                if (!device.IsActive) continue;
                foreach (var metric in metrics)
                {
                    if (!metric.IsActive)
                    {
                        // 停用指标：清除报警状态
                        device.SetMetricAlarmState(metric.Name, false);
                        continue;
                    }
                    double value = GenerateSmoothValue(device.Id, metric);

                    // 模拟数据同样只写暂存区 + 追加落库（保真）：三条数据源汇入同一路径，
                    // "报警判定 → 写报警状态 → 写值"的业务三步统一在心跳排空时执行（见 ApplyPendingSamples）
                    StageSample(device, metric.Name, value);
                }
            }
        }

        // 平滑随机游走：围绕上一值小幅波动，避免曲线剧烈抖动
        /// <summary>
        /// 为指定设备的指标生成一拍平滑随机游走值：以上一值为中心做量程 10% 以内的随机波动，
        /// 钳制在配置量程内并保留 1 位小数。
        /// </summary>
        /// <param name="deviceId">设备唯一标识，与指标名共同构成上一值缓存的键。</param>
        /// <param name="metric">指标配置，提供量程上下限。</param>
        /// <returns>本拍生成的模拟指标值；首次生成时从量程中点起步。</returns>
        private double GenerateSmoothValue(string deviceId, MetricConfig metric)
        {
            string key = $"{deviceId}:{metric.Name}";
            double span = metric.MaxValue - metric.MinValue;   // 指标量程，用于缩放波动幅度
            if (span <= 0) span = 1;                              // 防止除零/量程配置错误

            // 读取上一值作为本次起点（无历史则从量程中点开始）
            if (!_lastSimValues.TryGetValue(key, out double last))
                last = metric.MinValue + span * 0.5;

            // 波动幅度 = 量程的 10% × [-0.5, +0.5]，即单步最多变动量程的 5%
            double next = last + (_random.NextDouble() - 0.5) * span * 0.1;
            next = Math.Clamp(next, metric.MinValue, metric.MaxValue);  // 钳制在合法范围内
            _lastSimValues[key] = next;   // 回写，作为下一拍的起点
            return Math.Round(next, 1);   // 保留 1 位小数，与 UI 显示精度一致
        }

        // 返回设备列表快照（拷贝）：调用方拿到的是副本，后续对原集合的修改不影响它；
        // 这是跨线程暴露集合的安全做法——调用方可以自由遍历而无需加锁
        /// <summary>
        /// 返回设备列表的拷贝快照，供调用方跨线程安全遍历。
        /// </summary>
        /// <returns>当前设备集合的一份新副本；后续原集合的增删不影响该副本。</returns>
        public List<Device> GetDevices()
        {
            lock (_devicesLock)
            {
                return _devices.AsReadOnly().ToList();
            }
        }

        // MQTT 断线回调：只翻转旗标，真正的重连/降级在心跳 UpdateDevicesAsync 里处理
        /// <summary>
        /// MQTT 断线事件回调。
        /// </summary>
        /// <param name="sender">事件发送方，未使用。</param>
        /// <param name="e">事件参数，未使用。</param>
        /// <remarks>断线后 MQTT 数据自然停止更新（心跳不再生成模拟数据）；状态标记与重连策略由链路健康状态机接管。</remarks>
        private void OnMqttDisconnected(object? sender, EventArgs e)
        {
            // 断线后的状态标记与重连由链路健康状态机接管
        }

        // 心跳入口（UI 定时器每秒调一次）：本类对上层暴露的唯一"推进"方法。
        // 四件事按序执行：首次同步 → 生成/接收数据 → 批量落库 → WAL 回收
        /// <summary>
        /// 心跳推进入口（UI 定时器每秒调用一次），按序完成四件事：
        /// 首次设备/配置同步 → 模拟数据生成（MQTT 数据由事件独立驱动）→ 阈值批量落库 → 周期 WAL 回收。
        /// </summary>
        /// <returns>表示本拍异步处理的任务；对象已释放时立即完成。</returns>
        /// <remarks>
        /// 首次心跳且数据库可用时从库同步设备列表与指标配置（<c>_devicesLoaded</c> 保证只做一次）；
        /// 仅当所选数据源为"模拟数据"时生成模拟值，MQTT/串口通路本身不在此调用（事件驱动）；
        /// 缓冲达 BatchSize 时由 <see cref="FlushIfNeededAsync"/> 写库；距上次检查点满 60 秒时回收 WAL。
        /// </remarks>
        public async Task UpdateDevicesAsync()
        {
            if (_disposed) return;

            // 首次进入时从数据库拉设备列表和指标配置（只做一次，靠 _devicesLoaded 旗标短路）
            if (!_devicesLoaded && _databaseService != null)
            {
                await SyncDevicesFromDatabaseAsync().ConfigureAwait(false);

                if (_metricConfigs.Count == 0)
                {
                    await ReloadMetricConfigsAsync().ConfigureAwait(false);
                }
            }

            // 只有用户明确选择"模拟数据"时才生成模拟值；
            // 串口/MQTT 故障期间宁可停更（曲线不动、状态标记故障），也不拿假数据冒充真数据
            if (_selectedDataSource == DataSourceKind.Simulation)
            {
                UpdateDeviceData();
            }

            // 排空暂存区：模拟/MQTT/串口三条来源的样本在此统一执行"业务三步"（报警判定→写报警状态→写值→边沿通知）
            ApplyPendingSamples();

            // 先评估链路健康，再按新鲜度执行质量降级（写值瞬间已置 Good，此处仅降级）
            long nowMs = Environment.TickCount64;
            EvaluateLinkHealthAt(nowMs);
            TryStartReconnect();     // 重连启动与唤醒的统一入口：每拍检查一次，条件不满足时直接返回
            ApplyQualityDegradationAt(nowMs);

            // 三步汇合点的第三步在此统一冲刷：达到阈值才写库，摊薄磁盘 IO 开销
            await FlushIfNeededAsync().ConfigureAwait(false);

            // 每 60 秒回收一次 WAL 文件，防止 SQLite 临时文件无限增长
            await CheckpointIfNeededAsync().ConfigureAwait(false);
        }

        // D3：整体重建设备名索引。调用方必须已持有 _devicesLock
        /// <summary>
        /// 按设备名整体重建 <see cref="_devicesByName"/>（调用方必须已持有 <see cref="_devicesLock"/>）。
        /// </summary>
        /// <remarks>
        /// 只在设备集合发生结构变更（增删/改名）后调用，因此无需增量维护、也不会有陈旧条目；
        /// 同名设备取先出现者，与旧 <c>_devices.FirstOrDefault(d =&gt; d.Name == ...)</c> 的语义逐字一致。
        /// </remarks>
        private void RebuildDeviceIndex()
        {
            _devicesByName.Clear();
            foreach (var device in _devices)
            {
                if (!_devicesByName.ContainsKey(device.Name))
                    _devicesByName[device.Name] = device;
            }
        }

        // 增量同步设备列表：保留 Id 相同的旧对象（卡片 ViewModel 仍持有引用可继续收事件），
        // 移除已删除的，追加新增的。避免 Clear+重建导致引用失效和并发枚举异常。
        /// <summary>
        /// 从数据库增量同步设备列表到内存活体集合：删除库中已不存在的设备、追加新设备、
        /// 对 Id 相同的旧对象原地更新属性（不重建对象）。
        /// </summary>
        /// <returns>表示异步同步操作的任务；无数据库服务时立即完成。</returns>
        /// <remarks>
        /// 数据库读取在 <see cref="_devicesLock"/> 锁外执行（IO 期间不持锁，避免阻塞 MQTT/UI 线程），
        /// 仅集合增删改阶段进入临界区；删除采用倒序遍历避免索引位移跳过元素；
        /// 复用同 Id 旧对象是为了保持 UI 卡片持有的引用不断联，后续 SetMetricValue 事件仍能推送到卡片。
        /// 同步完成后置位 <c>_devicesLoaded</c>，使后续心跳跳过本段。
        /// </remarks>
        private async Task SyncDevicesFromDatabaseAsync()
        {
            if (_databaseService == null) return;
            // await 在锁外：数据库 IO 期间不持锁，避免阻塞 MQTT/UI 线程访问 _devices
            var dbDevices = await _databaseService.GetAllDevicesAsync().ConfigureAwait(false);

            // 进入临界区：下面对 _devices 的增删改必须串行化
            lock (_devicesLock)
            {
                var existingIds = _devices.Select(d => d.Id).ToHashSet();
                var dbIds = dbDevices.Select(d => d.Id).ToHashSet();

                // 倒序删除：正向删除会因索引位移跳过元素
                for (int i = _devices.Count - 1; i >= 0; i--)
                {
                    if (!dbIds.Contains(_devices[i].Id))
                    {
                        _devices.RemoveAt(i);
                    }
                }

                // 追加新增的（保留 Id 相同的旧对象，仅更新属性以保持引用不变）
                // 关键设计：不 new 新 Device——UI 卡片持有的是旧引用，复用它才能让
                // 后续 SetMetricValue 继续推送到卡片；重建对象会让卡片"断联"
                var existingByDeviceId = _devices.ToDictionary(d => d.Id);
                foreach (var dbDevice in dbDevices)
                {
                    if (existingByDeviceId.TryGetValue(dbDevice.Id, out var existing))
                    {
                        existing.Name = dbDevice.Name;
                        existing.DeviceType = dbDevice.DeviceType;
                        existing.Location = dbDevice.Location;
                        existing.Description = dbDevice.Description;
                        existing.IsActive = dbDevice.IsActive;
                    }
                    else
                    {
                        _devices.Add(dbDevice);
                    }
                }

                // D3：设备集合唯一的结构变更点（增删与改名都在上面的锁内完成），
                // 索引在此整体重建即可与 _devices 强一致，无需增量维护
                RebuildDeviceIndex();

                _devicesLoaded = true;   // 标记首次同步完成，下次心跳跳过本段
            }
        }

        /// <summary>
        /// 转发报警服务的无状态阈值越限判断（门面对外暴露）。
        /// </summary>
        /// <param name="metricName">指标名称。</param>
        /// <param name="value">待判定的指标当前值。</param>
        /// <returns>当前值是否处于报警态。</returns>
        public bool ShouldAlarm(string metricName, double value)
        {
            return _alarmService.ShouldAlarm(metricName, value);
        }

        /// <summary>
        /// 转发查询指定指标当前配置的报警阈值（门面对外暴露）。
        /// </summary>
        /// <param name="metricName">指标名称。</param>
        /// <returns>报警阈值；指标未配置时为 0。</returns>
        public double GetAlarmThreshold(string metricName)
        {
            return _alarmService.GetAlarmThreshold(metricName);
        }

        // UI 层"刷新设备列表"入口：先冲刷缓冲，再强制重新同步数据库
        /// <summary>
        /// UI 层"刷新设备列表"入口：先强制冲刷缓冲，再立即从数据库重新同步设备集合。
        /// </summary>
        /// <returns>表示异步刷新操作的任务。</returns>
        /// <remarks>与首次同步不同，本方法每次都执行增量同步（不检查 <c>_devicesLoaded</c>）；冲刷异常被隔离吞掉。</remarks>
        public async Task ReloadDevicesAsync()
        {
            try { await FlushAsync().ConfigureAwait(false); } catch { }
            await SyncDevicesFromDatabaseAsync().ConfigureAwait(false);
        }

        // ===== 批量写入缓冲与 WAL 回收 =====

        // 入队一条待落库记录：锁只保护"加进队列"这一步，不做实际 IO（快）
        /// <summary>
        /// 将一条数据记录加入落库缓冲队列（三步汇合点的第三步）。
        /// </summary>
        /// <param name="record">待缓冲的数据记录。</param>
        /// <remarks>
        /// 对象释放后静默丢弃；<see cref="_batchLock"/> 只保护"加入队列"这一步内存操作，
        /// 不做任何磁盘 IO，可被 UI 心跳线程与 MQTT 后台线程高频并发调用。
        /// </remarks>
        private void EnqueueDataRecord(DataRecord record)
        {
            if (_disposed) return;
            lock (_batchLock)
                _pendingBatch.Add(record);
        }

        // 阈值冲刷：缓冲达到 BatchSize 才写库；未达到则什么都不做。
        // 这是整个批量策略的核心——把高频小写入合并成低频大写入
        /// <summary>
        /// 阈值冲刷：仅当缓冲记录数达到 BatchSize 时取出整批并写库，否则不做任何操作。
        /// </summary>
        /// <returns>表示异步冲刷操作的任务；无数据库服务、已释放或未达阈值时立即完成。</returns>
        /// <remarks>
        /// 批量策略的核心——把高频小写入合并成低频大写入。
        /// 锁内只做"拷贝取出 + 清空队列"，实际写库在锁外执行，
        /// 避免慢速 IO 长时间持锁阻塞两条数据通路的入队。
        /// </remarks>
        private async Task FlushIfNeededAsync()
        {
            if (_databaseService == null || _disposed) return;

            List<DataRecord>? toFlush = null;
            // 锁内只做"取出+清空"，实际写库放锁外——写库慢，不能长时间持锁阻塞入队
            lock (_batchLock)
            {
                if (_pendingBatch.Count >= _batchSize)
                {
                    toFlush = new List<DataRecord>(_pendingBatch);  // 拷贝一份
                    _pendingBatch.Clear();                            // 原队列清空，继续接新数据
                }
            }
            if (toFlush != null)
                await WriteBatchAsync(toFlush).ConfigureAwait(false);
        }

        // 强制冲刷：不管缓冲多少条都立即写库（切换数据源/退出程序时调用，防丢数据）。
        // 与 FlushIfNeededAsync 的唯一区别：判断条件是 Count > 0 而非 >= BatchSize
        /// <summary>
        /// 强制冲刷：缓冲中只要还有记录就立即取出整批写库，不等待批量阈值。
        /// </summary>
        /// <returns>表示异步冲刷操作的任务；无数据库服务、已释放或缓冲为空时立即完成。</returns>
        /// <remarks>
        /// 在切换数据源、重新加载配置/设备以及程序退出时调用，防止在途数据丢失。
        /// 与 <see cref="FlushIfNeededAsync"/> 的唯一区别是触发条件为 Count &gt; 0；
        /// 同样遵循"锁内取数清空、锁外写库"的约定。
        /// </remarks>
        public async Task FlushAsync()
        {
            if (_disposed) return;

            await FlushCoreAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 冲刷内核：不检查释放标记，仅供 <see cref="Dispose"/> 在置位后兜底排空。
        /// </summary>
        /// <returns>表示异步冲刷操作的任务；无数据库服务或缓冲为空时立即完成。</returns>
        /// <remarks>与 <see cref="FlushAsync"/> 的唯一区别是不查 <c>_disposed</c>——否则"先置位再冲刷"会被门卫挡住。</remarks>
        private async Task FlushCoreAsync()
        {
            if (_databaseService == null) return;

            List<DataRecord>? toFlush = null;
            lock (_batchLock)
            {
                if (_pendingBatch.Count > 0)
                {
                    toFlush = new List<DataRecord>(_pendingBatch);
                    _pendingBatch.Clear();
                }
            }
            if (toFlush != null)
                await WriteBatchAsync(toFlush).ConfigureAwait(false);
        }

        // 真正执行批量写库的地方。用 SemaphoreSlim(1,1) 串行化——
        // 因为 FlushAsync 和 FlushIfNeededAsync 可能被不同路径同时调用，
        // SQLite 不允许并发写（WAL 模式虽可读写并发，但多写仍会冲突）
        /// <summary>
        /// 真正执行批量写库：经 <see cref="_flushLock"/> 串行化后调用数据库服务的单事务批量插入。
        /// </summary>
        /// <param name="records">已从缓冲取出、待整批写入的记录。</param>
        /// <returns>表示异步批量写入的任务。</returns>
        /// <remarks>
        /// <see cref="FlushAsync"/> 与 <see cref="FlushIfNeededAsync"/> 可能从不同路径并发触发，
        /// 而 SQLite/WAL 不允许多写并发，故用 SemaphoreSlim(1,1) 异步互斥；
        /// 异步等锁不阻塞 UI 线程，信号量在 finally 中确保释放，避免失败后永久阻塞后续写库。
        /// </remarks>
        private async Task WriteBatchAsync(List<DataRecord> records)
        {
            var db = _databaseService!;
            await _flushLock.WaitAsync().ConfigureAwait(false);   // 异步等锁：等待期间线程可干别的，不阻塞 UI
            try
            {
                await db.AddDataRecordsBatchAsync(records).ConfigureAwait(false);   // 单事务批量 INSERT
            }
            finally
            {
                _flushLock.Release();   // 无论成功失败都必须释放，否则后续写库永久阻塞
            }
        }

        /// <summary>
        /// 按指标名从当前配置快照查询单位。
        /// </summary>
        /// <param name="metricName">指标名称。</param>
        /// <returns>配置的指标单位；指标不存在或单位为 null 时返回空字符串。</returns>
        private string GetMetricUnit(string metricName)
            => _metricConfigsByName.TryGetValue(metricName, out var config)
                ? config.Unit ?? string.Empty   // 保留旧语义：Unit 为 null 时也返回空串
                : string.Empty;

        // 周期性 WAL 检查点：把 WAL 文件内容合并回主数据库并截断 WAL 文件。
        // WAL 模式下写操作先写 WAL，文件会持续增长；不回收会撑爆磁盘
        /// <summary>
        /// 周期性 WAL 检查点：距上次检查点满 60 秒时把 WAL 内容合并回主库并截断 WAL 文件。
        /// </summary>
        /// <returns>表示异步检查点判断与执行的任务；不满足周期条件、无数据库服务或已释放时立即完成。</returns>
        /// <remarks>
        /// 异常隔离：检查点失败只吞异常并照常更新时间戳，不阻断数据采集，
        /// 也避免失败时每拍重试造成抖动；最坏后果是 WAL 文件暂时偏大，下周期再试。
        /// </remarks>
        private async Task CheckpointIfNeededAsync()
        {
            if (_databaseService == null || _disposed) return;
            // 距上次检查点不足 60 秒则跳过——频率太高反而拖慢写性能
            if (DateTime.UtcNow - _lastCheckpointUtc < CheckpointInterval) return;

            try
            {
                await _databaseService.CheckpointAsync().ConfigureAwait(false);
            }
            catch
            {
                // WAL 检查点失败不应阻断数据采集——最坏情况是 WAL 文件稍大，下次再试
            }
            finally
            {
                // 无论成功失败都更新时间，避免失败时每拍都重试造成抖动
                _lastCheckpointUtc = DateTime.UtcNow;
            }
        }

        #region IDisposable
        /// <summary>
        /// 释放资源：先置位释放标记阻断所有入队路径，再兜底冲刷剩余缓冲，最后退订事件接线。
        /// </summary>
        /// <remarks>重复调用安全；冲刷异常被吞掉，保证释放流程不抛出。</remarks>
        public void Dispose()
        {
            if (_disposed) return;

            CancelReconnect();   // 窗口关闭时立即叫停后台重连循环

            // 退出时立即落盘剩余缓冲数据
            //关于这里,是不是该在清空之前停止心跳,否则心跳线程可能还在往_pendingBatch里加数据,导致数据丢失
            _disposed = true;   // 先关门：心跳/MQTT/串口的所有入队入口（EnqueueDataRecord 等）立即短路
            try { FlushCoreAsync().GetAwaiter().GetResult(); } catch { }   // 再扫地：专用内核不看 _disposed

            // 退订注入服务的接线（不 Dispose 注入进来的服务——其生命周期由 DI 容器负责）
            if (_mqttService != null)
            {
                _mqttService.MessageReceived -= OnMqttMessageReceived;
                _mqttService.Disconnected -= OnMqttDisconnected;
            }
            if (_serialService != null)
            {
                _serialService.DataReceived -= OnSerialRawData;
                _serialService.ConnectionStateChanged -= OnSerialConnectionStateChanged;
            }
            if (_frameParser != null) _frameParser.FrameParsed -= OnFrameParsed;
        }
        #endregion

        /// <summary>
        /// MQTT 消息负载的反序列化模型（私有嵌套）：设备 Id、设备名与指标名→指标值的键值集。
        /// </summary>
        private class MqttDeviceData
        {
            /// <summary>消息中的设备唯一标识。</summary>
            public string Id { get; set; } = string.Empty;
            /// <summary>设备名称，用于与内存设备集合匹配。</summary>
            public string Name { get; set; } = string.Empty;
            /// <summary>本设备上报的指标键值集（指标名 → 当前值）。</summary>
            public Dictionary<string, double> Metrics { get; set; } = new();
        }
    }
}
