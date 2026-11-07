# ARCHITECTURE

## 模块清单（一句话职责）
- MetalMonitor.UI：WPF 界面层，App.xaml.cs 持有 DI 注册表
- MetalMonitor.core：业务与数据层（DeviceService 门面 / DatabaseService 落盘 / MqttService、SerialService 数据源 / FrameParser+FrameRouter 协议栈 / ConfigService 配置【DI 单例】）
- MetalMonitor.Tests：xUnit + Moq，经 InternalsVisibleTo 访问 internal 测试构造函数

## 依赖方向
UI → core（接口）；core 内部 DeviceService 依赖各服务接口 + ConfigService 单例；Tests → core

## 数据源状态出口（STEP-3 起）
DeviceService.SetDataSourceAsync / InitializeDataSourceAsync → State（Online/Faulted/Unavailable）→ DataSourceStateChanged 事件 → MainViewModel 写 AlarmLogs（封送 UI 线程）。用户所选数据源与 config.json 不再被故障改写；心跳仅在所选数据源为"模拟数据"时生成模拟值（D-4/D-5）。

## 链路健康与数据质量（STEP-9 起，T4.1 全线）
- 链路健康（LinkHealth，D-8/D-9/D-12）：DeviceService 维护 4 个单调毫秒时间戳（连接时刻 / 最近任意字节 / 最近有效帧；判据纯函数在 DataSource.cs 的 LinkHealthEvaluator）→ **六态** Inactive/Recovering/Healthy/NoValidFrame/NoSignal/Reconnecting → 仅跃迁时广播 LinkHealthChanged。
- 指标质量（MetricQuality，D-10）：Device 为每个指标保存写入时刻与质量码 → 写值瞬间置 Good；心跳 UpdateDevicesAsync 在派发后调用 ApplyQualityDegradationAt 只做降级（Stale/Bad）。
- 自动重连（STEP-11，D-13/D-14）：时钟驱动，心跳判定 NoSignal 且无重连在跑时启动 ReconnectLoopAsync → 指数退避无限重试（ReconnectBackoff 纯函数，base*2^attempt 封顶 max，±20% 抖动）→ 成功归零退出、切源/Dispose CancelReconnect 立即取消；仅 NoSignal（无任何字节）重连，NoValidFrame 不重连（线通，重连修不了波特率/协议错）。配置键 Serial.ReconnectBaseSeconds（默认 1）/ ReconnectMaxSeconds（默认 30）。
- 端口漂移与连接验证（STEP-19/20，D-18）：候选端口序列 = 持久化 PortName → KnownPorts（最多 5，最近成功置顶）→ 兜底（候选为空则遍历全部可用端口；候选全不在场且系统仅一个端口则试该端口）；两处连接路径（切源初始连接、重连循环）都遍历候选。**ConnectAsync 成功不再等于连接成功**：需在 WatchdogSeconds 窗口内 `_lastSerialValidFrameMs` 前进才判通过，通过才 SetState(Online) 并 RecordSerialPortSuccess（写 PortName + KnownPorts）；未通过则断开试下一个。唤醒判据（STEP-20 修正）：启动循环用"记忆名单任一端口在场"（SerialPortAppeared），唤醒已在跑的循环用"相对上一拍新增的端口命中名单"（SerialPortNewlyAppeared，边沿语义，防每拍唤醒打穿退避）。代价：每次候选验证约 3 秒；防不住同固件同 NODE_ID 的设备（身份认证留 T5.x）。
- 配置口径：Serial.WatchdogSeconds / StaleSeconds（默认 3）+ ReconnectBaseSeconds / ReconnectMaxSeconds（默认 1 / 30）；均启动只读，ConfigService 归一化到 [1,60]，Max<Base 时自动对齐。
- UI 呈现（STEP-10）：IDeviceService 暴露 LinkHealth/LinkHealthChanged；顶栏标题旁两行状态（数据源状态/链路健康，DataTrigger 按枚举值染色）；Reconnecting 与 Recovering 同琥珀色；卡片数值按质量码染色（Stale 琥珀/Bad 灰）+ 右上"陈旧/不可信"角标（Good 隐藏）。枚举→文案在 MainViewModel 翻译，枚举→颜色在 XAML DataTrigger——Core 不携带任何 UI 语义。
- 端口重现唤醒（D-17 / STEP-18，STEP-20 放宽判据）：循环已在跑时，若记忆名单中的端口"刚刚出现"则唤醒（边沿语义，见上条）；端口漂移（换 USB 口导致 COM 号变化）由 STEP-19/20 候选端口序列承接。

## 典型数据流（串口通路）
SerialPort 字节 → SerialService.DataReceived → FrameParser（环形缓冲+状态机+CRC）→ FrameRouter（NODE_ID 路由）→ DeviceService 暂存区（latest-wins）→ 心跳 1Hz 派发 → Device 事件 → UI 卡片/曲线；同步追加落库缓冲（50 条批量写 SQLite WAL）

## 技术栈选型理由
- WPF + MVVM：工控上位机桌面场景
- SQLite WAL：单机落盘、读写并发（D 见 PROJECT_CONTEXT 技术债表）
- Microsoft.Extensions.DI：换数据库等模块化需求（用户初衷：SQLite→MySQL 可替换）

## 编码约定

### sealed 约定（D-7，2026-10-22）
- EventArgs 派生类：一律 sealed（STEP-6 已统一全部 5 个）
- 纯数据模型类与服务实现类：所属任务验收 DONE 时顺手封存（DeviceService 等待 T4.x 验收）
- XAML 代码隐藏（Window/UserControl/App）与测试类：遵循框架惯例，不加
- 拆封条件：出现真实继承需求或需运行时子类化（EF 代理、Moq 类 mock）时拆封，并在 DECISIONS.md 记录

### 线程纪律："谁碰 UI 谁负责回家"
- Core 层所有 await 一律 ConfigureAwait(false)：延续不假设任何上下文，且不碰任何 UI 对象，只发事件
- UI 层事件处理器不假设自己在 UI 线程（Core 事件可能落在池线程），改集合/属性前先 Dispatcher.CheckAccess()，不在则 BeginInvoke 封送
- UI 层内部 await 若延续要碰 UI（如改 ObservableCollection），则**故意不加** ConfigureAwait(false)，让默认行为送回 UI 线程
