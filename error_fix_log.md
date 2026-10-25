# MetalMonitor 错误修复日志

> 本文件记录项目开发过程中遇到的所有问题、成因、解决方法及相关代码位置。

---

## 一、架构与设计问题

### 1. ViewModel 中直接 new 对象导致紧耦合

- **时间**: 项目早期
- **文件**: `MetalMonitor.UI/ViewModels/` 下多个 ViewModel
- **成因**: 直接在 ViewModel 中 `new` 服务对象，导致紧耦合，难以测试和扩展
- **解决方法**: 引入依赖注入（DI）容器，通过构造函数注入依赖接口（`IDeviceService`、`IDatabaseService`、`IAlarmService` 等），服务注册集中在 `App.xaml.cs`
- **约束**: 依赖接口而非具体实现，业务逻辑放在 Core 层，ViewModel 仅负责数据适配和 UI 逻辑

### 2. 事件订阅未取消 / Timer 未释放导致内存泄漏

- **时间**: 项目早期
- **文件**: `MetalMonitor.UI/ViewModels/MainViewModel.cs`、`MetalMonitor.UI/ViewModels/DeviceCardViewModel.cs` 等
- **成因**: 事件订阅后未取消订阅，`System.Timers.Timer` 未释放，导致对象无法被 GC 回收
- **解决方法**: 实现 `IDisposable` 接口，在 `Dispose` 中取消事件订阅（`-=`）并释放 Timer；`MainViewModel` 和 `DeviceCardViewModel` 均实现 `IDisposable`
- **⚠️ 关联复核（2026-10-24，第 2 次同类）**: `DeviceService` 同样只退订了串口两个事件，`MessageReceived` / `Disconnected` 自订阅起从未退订（详见 #38）。说明本条的教训当时只落实到 ViewModel 层，服务层未同步——**凡订阅必配对退订，应按"订阅处同一屏内能搜到退订"自查**

### 3. DeviceService 中 Clear() 与定时器 foreach 并发触发 InvalidOperationException

- **时间**: 项目中期
- **文件**: `MetalMonitor.core/Services/DeviceService.cs`
- **函数**: `UpdateDeviceDataAsync`、设备列表同步逻辑
- **成因**: `DeviceService` 对 `_devices` 集合执行 `Clear()` 后重建，与定时器线程的 `foreach` 遍历并发，触发 `InvalidOperationException` 并终止进程
- **解决方法**: 引入 `_devicesLock` 锁对象保护 `_devices` 列表；遍历前获取快照（`lock` 内 `ToList()`）；改为增量同步机制（按 Id 保留/移除/追加），避免 `Clear()` + 重建

### 3b. 【关联 #1】DI 容器多构造函数歧义导致启动卡死（无窗口、调试不终止）

- **时间**: 2026-09-05 16:45:15（发现并修复于同日）
- **文件**: `MetalMonitor.core/Services/DeviceService.cs` — 构造函数区（当前版本约第 36-78 行）；异常抛出点 `MetalMonitor.UI/App.xaml.cs` — `OnStartup`（第 71 行 `GetRequiredService<IDeviceService>()`）
- **现象**: 程序启动后不显示主窗口、任务管理器中进程存活、VS 调试不中断也不报错，表现为"卡死"。`logs/app_2026-09-05_16-45-15.log` 记录到 `System.InvalidOperationException: Unable to activate type 'MetalMonitor.Core.DeviceService'. The following constructors are ambiguous: .ctor(IAlarmService, IMqttService, IDatabaseService) 与 .ctor(IAlarmService, ConfigService)`
- **根本原因**: 为支持单元测试，给 `DeviceService` 新增了带 `ConfigService` 参数的 public 构造函数，其中一个还写成可选参数（`ConfigService? configService = null`）。.NET DI 容器解析服务时按"参数最多且全部可解析"选择构造函数，而**带默认值的可选参数也会被容器判定为"可满足"**，导致两个构造函数同时命中、容器无法决断而抛歧义异常。异常发生在 `async void OnStartup` 中，被 `DispatcherUnhandledException` 捕获后置 `e.Handled = true`：异常被吞掉、进程继续存活，但 `mainWindow.Show()` 永远不会执行——于是"无界面、无崩溃、无调试中断"。
- **解决方法**:
  1. public 构造函数集合恢复为 DI 可唯一决断的形态（最长构造函数唯一：`(IAlarmService, IMqttService, IDatabaseService)`），**public 构造函数不使用可选参数**；
  2. 仅供单元测试的构造函数改为 `internal`（DI 容器只反射 public 构造函数，天然不可见），并在 `MetalMonitor.core.csproj` 添加 `<InternalsVisibleTo Include="MetalMonitor.Tests" />` 对测试工程开放；
  3. 公共初始化逻辑抽取为 `InitializeBatchSettings()` 避免构造函数重复代码。
- **教训/最佳实践**:
  - DI 容器解析的类型，public 构造函数必须保持"唯一最长可解析"；测试专用重载一律 `internal` + `InternalsVisibleTo`，这是 .NET 项目的标准做法；
  - `async void` 仅限事件处理器，且其中的异常会进入全局异常处理器——全局处理器 `e.Handled = true` 会让启动期失败"静默存活"，排障时第一时间看 `logs/` 滚动日志；
  - 本次能快速定位，正是因为 #17 引入的全局异常落盘机制生效。
- **最后校验版本**: 2026-09-05 修复后，48 项单元测试全部通过，实际启动验证主窗口正常显示。

---

## 二、数据采集与存储问题

### 4. 逐条插入 SQLite 导致大量连接和锁冲突

- **时间**: 2026-09-02 16:08
- **文件**: `MetalMonitor.core/Services/DeviceService.cs` — `UpdateDeviceDataAsync`；`MetalMonitor.core/Services/DatabaseService.cs` — `InitializeAsync`
- **成因**: 每秒定时器对 3 个设备 × 6 个指标共 18 条数据逐条插入，每条新建独立 SQLite 连接，默认 rollback journal 模式下写入持有独占锁，与设置窗口的读写产生冲突；同时 `async void` 定时器回调无防重入，导致回调堆叠
- **异常类型**: `Microsoft.Data.Sqlite.SqliteException`
- **解决方法**:
  1. `DatabaseService.InitializeAsync` 中执行 `PRAGMA journal_mode=WAL` 启用 WAL 模式允许读写并发
  2. `UpdateDeviceDataAsync` 改为收集所有记录后通过 `AddDataRecordsBatchAsync` 批量插入（单事务）
  3. `MainViewModel` 定时器回调使用 `Interlocked.CompareExchange` 实现防重入
- **约束**: SQLite 必须启用 WAL 模式；数据记录采用批量插入（缓存 50 条自动写入，切换指标/设备/页面或关闭窗口时立即写入剩余数据）；定时器回调需实现防重入机制

---

## 三、MQTT 通信问题

### 5. 未运行本地 MQTT Broker 时连接 localhost 导致 SocketException

- **时间**: 项目早期
- **文件**: `MetalMonitor.core/Services/MqttService.cs`
- **成因**: 未运行本地 MQTT Broker 时，连接 `localhost` 抛出 `SocketException`
- **解决方法**: 连接失败时捕获异常并回退到模拟数据模式；MQTT 连接断开时自动切换回模拟数据；支持多 Broker 自动切换（EMQX、HiveMQ、Mosquitto），超时 5 秒切换下一个
- **约束**: MQTT 连接需支持多 Broker 自动切换
- **⚠️ 关联复核（2026-10-14，由用户主动质疑"当初的静默回退是否合理"触发；本条与第 5 条同源，累计第 2 次）**: 本条的"解决方法"本身**制造了一个新的反模式**。现 `DeviceService.SetDataSourceAsync` 与 `InitializeMqttAsync` 在 MQTT 连接失败时执行 `_useMqtt = false; _selectedDataSource = "模拟数据"; _configService.DataSource = "模拟数据"`（现 L187-193 / L227-233），即"失败 → 静默换成模拟数据 → 并把这次失败**持久化写进 config.json**"。后果有二：
  1. **失效不透明（fail-silent）**：`DeviceService` 不实现 `INotifyPropertyChanged`、也不暴露任何事件（该类 `public event` 计数为 0），而 `MainViewModel._selectedDataSource` 仅在构造时（MainViewModel.cs L126）从服务读一次，此后只有用户操作才改。故服务在后台悄悄改用模拟数据时，**UI 下拉框仍显示"MQTT"**，操作员无从察觉。
  2. **把失败伪装成用户意图**：`_configService.DataSource = "模拟数据"` 会立即写盘，一次临时的 broker 未启动会被**永久**记为用户偏好，此后每次启动都直接是模拟数据——失败被"洗"成了配置。
  - **同类残留（本次审计一并登记）**：`case "串口"`（现 L242-249）只做 `_useMqtt=false` 而未接串口通路，心跳 `UpdateDevicesAsync`（现 L520）的 `if (!_useMqtt || !_mqttService?.IsConnected == true) UpdateDeviceData();` 于是继续生成模拟值——**用户选了"串口"，看到的却是模拟数据**；且该断线判定以 `_mqttService.IsConnected` 为唯一事实来源，没有"当前数据源是哪一个"的分支，串口接入后必须重写。
  - **拟采用方案**：①删除内部静默回退，"失败即停数据并暴露原因"；②数据源状态收敛为**单一权威源**（服务持有并广播变更事件，VM 不再缓存副本）；③禁止在失败路径上写盘覆写用户配置；④`case "串口"` 随 T3.4 第⑤步修（该段本就要重写，成本≈0），回退链与权威源改造随 T4.1 修（断线三态 UI 本就要动这块）。
  - **教训/最佳实践**: 把"连通性失败"的兜底写成"替换数据源"，是把**可用性**问题误当成**数据正确性**问题解决。容错的正确形态是**降级且可见**（停更新 + 明确报错 + 质量码 Bad），不是**替换且静默**。关联通用原则：任何"多副本状态"（VM 字段 / 服务旗标 / 磁盘配置 / UI 控件）都必须收敛到单一权威源并广播变更，否则必然漂移。
- **⚠️ 关联复核（第 3 次 / 2026-10-14，由用户主动指出"很多错误处理都是直接留空的，我认为不妥"并主张做错误可见化而触发）**: 本类问题（**静默失效 / fail-silent**）累计已达 **3 次**：① 第 1 次 = #3b（全局处理器 `e.Handled = true` 吞掉启动期异常 → 无窗口、无崩溃、无报错的"卡死"）；② 第 2 次 = 本条上方的关联复核（MQTT 失败静默回退模拟数据并把失败写盘固化为用户偏好）；③ 第 3 次 = 本次**全项目静默 catch 审计**。按本文件规则，同类 ≥3 次须补充详尽描述（触发条件 / 影响范围 / 多条解决路径 / 最佳实践）。
  - **触发条件**: 任何被 `try { ... } catch { }`（或 catch 体内仅有注释、仅 `Console.WriteLine`/`Debug.WriteLine`、仅计数不上报）包裹的代码路径抛出异常时——异常被吞掉，调用方按"成功"继续执行，故障不留证据。
  - **影响范围（本次审计定位 8 处；行号以 2026-10-14 工作区版本为准，HEAD=`bd2153`，工作区尚有未提交改动）**:

    | # | 位置（文件 · 方法 · 行） | catch 内实际行为 | 失败时的用户可见后果 | 建议定级 |
    |---|---|---|---|---|
    | 1 | `MetalMonitor.core/Services/DeviceService.cs` · `SetDataSourceAsync` · L206-233 | `FlushAsync` 异常吞掉；MQTT 连接失败静默回退模拟数据 | 数据源自己跳回"模拟数据"，无任何解释 | Error |
    | 2 | `MetalMonitor.core/Services/DeviceService.cs` · `SetDataSourceAsync` 断开分支 · L242-255 | `DisconnectAsync` 异常吞掉 | 断连失败无感知 | Warning |
    | 3 | `MetalMonitor.core/Services/DeviceService.cs` · `ReloadMetricConfigsAsync` · L270-278 | `FlushAsync` 异常吞掉 | 配置重载失败仍用旧配置，无提示 | Warning |
    | 4 | `MetalMonitor.core/Services/DeviceService.cs` · `ReloadDevicesAsync` · L616-620 | 冲刷缓冲异常吞掉 | 点"刷新"后若写盘失败，无任何反馈 | Warning |
    | 5 | `MetalMonitor.core/Services/SerialService.cs` · 串口接收异常处理 · L126-130 | 仅 `Console.WriteLine`（注释明示"不能冒泡到全局处理器"） | **界面全无提示**，只有控制台 | Error |
    | 6 | `MetalMonitor.core/Services/RollingFileLogger.cs` · 日志写入失败处理 · L84-101 | 静默 | —（**故意静默，不得改**） | 保持 |
    | 7 | `MetalMonitor.UI/ViewModels/MainViewModel.cs` · 每秒刷新回调 · L160-228 | 异常追加进 `AlarmLogs` | 与业务报警混在一起，不可区分 | 分流到错误中心 |
    | 8 | `MetalMonitor.UI/App.xaml.cs` · 全局兜底 · L47-52 | `MessageBox.Show` + 落盘 | 现有唯一像样的提示，需接入中枢 | Fatal |

  - **多种解决路径**:
    - **路径 A（用户已选定）· 一处收集、多处呈现**: Core 新增 `IErrorReportService`（分级 / 内存环形列表 / 按 (source, 异常类型) 去重聚合 / 事件广播），UI 三个出口——徽标（红点数字 = **当前活跃故障源数**）、错误中心页（**仅查看与展示**）、弹窗（**同源同类型每会话仅提示一次**）。**内存态、不持久化**，重启后新错误重新提示。系统故障与设备业务报警**分离**（前者类比 IDE 对代码逻辑的报错，后者是设备传上来的数据报警）。
    - **路径 B · 最小可见化**: 不动架构，只给 8 处 catch 加统一日志 + 一条状态栏文本。成本最低，但"每会话提示一次"与红点计数需另造。
    - **路径 C · 引入日志框架**: 换 `Microsoft.Extensions.Logging` + `ILogger<T>` 统一抽象，错误中枢作为 sink。更标准，但为本项目引入额外依赖与一次重构。
  - **最佳实践**: 容忍的合法形态只有"**降级且可见**"，不是"**替换且静默**"。一个 `catch` 空着只有三种正当理由：(1) 已可预期的业务分支；(2) 记录并**上报**；(3) **该操作自身失败不得影响主流程**——如 #6 日志写入，此时必须保留静默（否则"写日志失败→报错→再写日志→又失败"会递归）。这条即是"故意静默"与"疏忽静默"的分界线。另：弹窗必须按 (来源+类型) 去重，否则高频错误会引发**弹窗风暴**，用户被训练成无脑点"确定"，提示彻底失效；定级方法——问一句"用户此刻能不能什么都不做"，不能=必须可见（弹窗），能但不该被蒙在鼓里=进徽标。
  - **排期（用户决策 2026-10-14）**: **仅登记，不插队**。不到达"完善阶段"不展开任务明细。清单与四项设计决议已登记于 `PROJECT_CONTEXT.md §P4` 与 `PROJECT_PLAN.md §C4`。
  - **退场/反转条件**: ① 替换 catch 过程中出现任何原功能回归 → 立即回滚该处，只保留新增中枢与全局兜底；② 错误中心页 UI 超预算 → 砍页面与徽标，保留基础层（全链路错误可见才是真价值）；③ "内存态不持久化"若被证明影响排障（重启后丢失现场）→ 再引入落盘。

### 6. 公共 MQTT Broker 使用通用主题接收大量无关消息

- **时间**: 项目早期
- **文件**: `MetalMonitor.core/Services/DeviceService.cs` — `InitializeMqttAsync`、`SetDataSourceAsync`
- **成因**: 使用公共 MQTT Broker 时，通用主题（如 `devices/#`）会接收大量无关设备的消息
- **解决方法**: 使用唯一命名空间主题 `metalmonitor/devices/#`，只接收本项目设备的数据
- **约束**: MQTT 消息主题必须使用唯一命名空间 `metalmonitor/devices/#`

---

## 四、UI 与图表控件问题

### 7. LiveCharts 图表控件捕获鼠标事件导致点击无法触发

- **时间**: 项目早期
- **文件**: `MetalMonitor.UI/MainWindow.xaml` / `DeviceCardView.xaml`
- **成因**: LiveCharts 图表控件会捕获鼠标事件，导致 `MouseLeftButtonUp` 事件无法触发，设备卡片点击无效
- **解决方法**: 改用 `PreviewMouseLeftButtonDown` 隧道事件（预览事件），并设置 `e.Handled = true` 阻止图表控件吞没事件

### 8. XAML 中引用不存在的资源键导致 XamlParseException

- **时间**: 项目早期
- **文件**: 多个 XAML 文件
- **成因**: XAML 中引用了不存在的资源键（如 `ForegroundBrushKey`），导致 `XamlParseException`
- **解决方法**: 确保使用正确的系统颜色资源键名：`SystemColors.WindowTextBrushKey`（而非 `ForegroundBrushKey`）；所有自定义资源在 `App.xaml` 的 `Application.Resources` 中统一定义

### 9. 深色主题下 UI 控件样式不适配

- **时间**: 项目早期
- **文件**: `MetalMonitor.UI/App.xaml`、多个窗口 XAML
- **成因**: 深色主题下，默认系统颜色导致控件背景/文字颜色不协调
- **解决方法**: 在 `App.xaml` 中覆盖系统画笔（`WindowBrushKey`、`ControlBrushKey`、`WindowTextBrushKey` 等）；自定义 ComboBox、TabItem、CheckBox、DataGrid、ScrollBar 等控件模板；图表控件设置 `Background="Transparent"` 适配深色卡片背景
- **约束**: ComboBox 背景 `#FF3D3D4F`，文字 `#FFE0E0E0`，选中项背景 `#FF4A90D9`；图表 X 轴显示时间格式（HH:mm:ss），Y 轴根据指标类型显示对应单位（℃、V、件、%、MPa、rpm）

### 10. 可视化趋势曲线显示异常（Y 轴范围硬编码）

- **时间**: 2026-09-02 15:59
- **文件**: `MetalMonitor.UI/ViewModels/DeviceCardViewModel.cs` — `ApplyMetricLimits`；`MetalMonitor.UI/ViewModels/DeviceDetailViewModel.cs` — `UpdateChart`/`GetMetricMinMax`
- **成因**: 图表 Y 轴范围硬编码，调整阈值或最值后曲线裁剪异常，无法正确展示过高或过低的数据
- **解决方法**: 将 Y 轴范围从硬编码改为使用指标设置中的 `MinValue` 和 `MaxValue`；`MainViewModel` 定时器每秒调用 `ApplyMetricLimits` 刷新所有卡片 Y 轴范围，实现配置变更自动生效

---

## 五、设备管理与数据同步问题

### 11. 删除设备后引发 System.ArgumentNullException

- **时间**: 2026-09-03 10:35
- **文件**: `MetalMonitor.UI/ViewModels/DeviceDetailViewModel.cs`
- **函数**: `SelectedMetricName` setter、`UpdateCurrentValueText`
- **成因**: 设备删除后指标配置清空，导致 `SelectedMetricName` 被设为 `null`，进而在 `UpdateCurrentValueText` 中调用 `_device.GetMetricValue(null)` 抛出 `ArgumentNullException`
- **解决方法**: 在 `SelectedMetricName` setter 中添加 null/空字符串检查；`UpdateCurrentValueText` 增加防御性检查

### 12. 删除设备后其他设备卡片图表被清空重新累计

- **时间**: 2026-09-03 10:35
- **文件**: `MetalMonitor.UI/ViewModels/MainViewModel.cs` — `OnTimerElapsed` Dispatcher 回调
- **成因**: 原逻辑在设备数量变化时整体重建所有卡片（`Clear()` + 重新 `Add`），导致原有设备卡片的历史曲线数据丢失
- **解决方法**: 改为按设备 Id 增量同步卡片：仅移除已删除设备的卡片（`Dispose` + `RemoveAt`），保留其他设备的卡片对象及历史曲线数据；新增设备追加新卡片

### 13. 添加设备后卡片不立即展示

- **时间**: 2026-09-03 10:56
- **文件**: `MetalMonitor.UI/Windows/DeviceManagementWindow.xaml.cs` — `OnClosed`（原第 38-43 行）；`MetalMonitor.core/Services/DeviceService.cs` — `UpdateDevicesAsync`
- **成因**: 设备列表同步只在 `DeviceManagementWindow.OnClosed` 中以 fire-and-forget 方式触发（`_viewModel.ReloadMainDeviceListAsync().ConfigureAwait(false)`），且 `DeviceService.UpdateDevicesAsync` 仅在首次加载时同步数据库（`!_devicesLoaded`），导致新增设备写入数据库后主界面无法感知
- **解决方法**:
  1. 在 `DeviceManagementViewModel` 的 `AddDeviceAsync`、`UpdateDeviceAsync`、`DeleteDeviceAsync` 中，数据库操作完成后立即 `await _deviceService.ReloadDevicesAsync()`，使主界面定时器下次刷新即可检测到设备变化
  2. 移除 `OnClosed` 中的 fire-and-forget 调用（增删改操作已即时同步，关闭时无需再刷新）

### 14. SystemSettingsWindow 的 fire-and-forget 导致配置不即时生效

- **时间**: 2026-09-03 11:20
- **文件**: `MetalMonitor.UI/Windows/SystemSettingsWindow.xaml.cs` — `OnClosed`（原第 41 行）；`MetalMonitor.UI/ViewModels/SystemSettingsViewModel.cs` — `AddMetricConfigAsync`/`UpdateMetricConfigAsync`/`DeleteMetricConfigAsync`
- **成因**: `SystemSettingsWindow.OnClosed` 中 `_viewModel.ReloadServiceConfigsAsync().ConfigureAwait(false)` 是未 await 的 fire-and-forget 调用，指标配置变更不即时同步到设备服务
- **解决方法**: 在 `SystemSettingsViewModel` 的增删改操作后即时 `await _deviceService.ReloadMetricConfigsAsync()`；移除 `OnClosed` 中的 fire-and-forget

### 15. SystemSettingsViewModel 中 NullReferenceException

- **时间**: 2026-09-02 15:45
- **文件**: `MetalMonitor.UI/ViewModels/SystemSettingsViewModel.cs` — `UpdateMetricConfigAsync`（原第 204 行）
- **成因**: `LoadMetricConfigsAsync()` 内部执行 `MetricConfigs.Clear()` 时，DataGrid 的 `SelectedItem` 被置为 `null`，双向绑定的 `SelectedMetricConfig` 也变为 `null`，随后访问 `SelectedMetricConfig.Name` 抛出空引用异常
- **解决方法**: 在调用 `LoadMetricConfigsAsync()` 之前用局部变量 `updatedName` 捕获名称，后续状态消息使用该局部变量

---

## 六、窗口关闭与焦点问题

### 16. 删除设备后关闭管理窗口闪退（fire-and-forget 导致）

- **时间**: 2026-09-03 10:56
- **文件**: `MetalMonitor.UI/Windows/DeviceManagementWindow.xaml.cs` — `OnClosed`（原第 41 行）
- **成因**: `_viewModel.ReloadMainDeviceListAsync().ConfigureAwait(false)` 是未 await 的 fire-and-forget 调用，在窗口析构阶段启动后台异步操作，任何异常都成为未观察的 Task 异常，导致无报错闪退
- **解决方法**:
  1. 移除 `OnClosed` 中的 fire-and-forget 调用
  2. 在 `MainViewModel` 定时器 Dispatcher 回调中包裹 try-catch，防止 UI 线程异常闪退

### 17. 缺少全局异常处理

- **时间**: 2026-09-03 11:00
- **文件**: `MetalMonitor.UI/App.xaml.cs`（原第 1-46 行，无任何异常处理）
- **成因**: 应用无全局异常处理（`DispatcherUnhandledException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException` 均未订阅），任何未处理异常直接导致 VS 调试器中断闪退
- **解决方法**: 在 `App` 构造函数中订阅三个全局异常事件：UI 线程异常设置 `e.Handled = true` 阻止闪退并 MessageBox 提示；非 UI 线程异常记录日志；未观察 Task 异常标记 `SetObserved()`。所有异常信息记录到 `error_fix_log.md`

### 18. async void 事件处理器无 try-catch

- **时间**: 2026-09-03 11:00
- **文件**: `MetalMonitor.UI/Windows/DeviceManagementWindow.xaml.cs` — `OnAddDevice`（原第 19 行）、`OnUpdateDevice`（原第 24 行）、`OnDeleteDevice`（原第 29 行）、`Loaded` lambda（原第 16 行）
- **成因**: `async void` 事件处理器中的异常无法被调用方捕获，直接传播为未处理异常
- **解决方法**: 在所有 `async void` 事件处理器中加 try-catch，捕获异常后以 MessageBox 提示用户

### 19. 删除设备后关闭管理窗口焦点跳到 VS（真正根因）

- **时间**: 2026-09-03 11:20
- **文件**: `MetalMonitor.UI/Windows/DeviceManagementWindow.xaml.cs` — `OnClosed`（第 58-70 行）
- **函数**: `OnClosed`
- **成因**: 并非异常导致，而是**窗口焦点/激活问题**。设备管理窗口用 `Show()`（非模态）打开，设置 `Owner = MainWindow`。当非模态窗口关闭时，Windows 不一定可靠地把焦点还给 Owner，在 VS 调试环境下更容易跳到 VS 界面。表现为：无报错、无 MessageBox、VS 调试未中止、主窗口仍在但未获焦点
- **解决方法**: 在 `OnClosed` 中 `base.OnClosed(e)` 之后显式激活 Owner（主窗口）：
  1. `Owner.Activate()` — 请求激活主窗口
  2. `Owner.Topmost = true; Owner.Topmost = false;` — Topmost 技巧确保窗口被提到前台
  3. `Owner.Focus()` — 设置键盘焦点
- **同步修复**: 对 `DeviceDetailWindow.xaml.cs` 和 `SystemSettingsWindow.xaml.cs` 的 `OnClosed` 应用相同修复
- **约束**: 非模态窗口（`DeviceManagementWindow`、`DeviceDetailWindow`、`SystemSettingsWindow`）关闭时需显式激活主窗口，确保焦点正确返回

---

## 七、经验教训总结

| 编号 | 教训 | 对应问题 |
|------|------|----------|
| 1 | 直接在 ViewModel 中 new 对象会导致紧耦合 | #1 |
| 2 | 事件订阅后需取消订阅，Timer 需释放 | #2 |
| 3 | 对共享集合的 Clear() 与遍历并发会触发 InvalidOperationException | #3 |
| 4 | 逐条插入 SQLite 会导致大量连接和锁冲突，应采用批量插入单事务模式 | #4 |
| 5 | SQLite 必须启用 WAL 模式以允许读写并发 | #4 |
| 6 | 定时器回调需实现防重入机制 | #4 |
| 7 | 未运行本地 MQTT Broker 时连接 localhost 会抛 SocketException | #5 |
| 8 | 公共 MQTT Broker 使用通用主题会接收大量无关消息，需用唯一命名空间 | #6 |
| 9 | LiveCharts 会捕获鼠标事件，需用 Preview 隧道事件 | #7 |
| 10 | XAML 中引用不存在的资源键会导致 XamlParseException | #8 |
| 11 | 深色主题需覆盖系统画笔并自定义控件模板 | #9 |
| 12 | 图表 Y 轴范围不应硬编码，应绑定指标配置 | #10 |
| 13 | 集合 Clear() 时 DataGrid 的 SelectedItem 会变为 null，需提前用局部变量捕获 | #15 |
| 14 | 设备数量变化时应增量同步卡片，避免 Clear()+重建导致历史数据丢失 | #12 |
| 15 | fire-and-forget 异步调用会产生未观察 Task 异常，应 await 或改在操作中即时调用 | #13, #14, #16 |
| 16 | 应用需订阅全局异常事件，防止未处理异常直接闪退 | #17 |
| 17 | async void 事件处理器中的异常无法被捕获，需加 try-catch | #18 |
| 18 | 非模态窗口关闭时需显式激活 Owner，否则焦点可能跳到其他应用 | #19 |
| 19 | DI 容器解析的类型，public 构造函数须"唯一最长可解析"，测试重载用 internal + InternalsVisibleTo；可选参数也会被 DI 视为可满足而引发歧义 | #3b（关联 #1） |
| 20 | 元件"用不了"先分清是**物理不匹配**还是**操作不会**：孔距/封装/电压域对不上时，正确动作是换方案或止损，不是加倍练习；同时 AI 不得凭照片臆测丝印，必须以用户口述或实测为准 | #23 |
## [2026-09-05 16:56:32] SqliteException (UI线程未处理异常)
- 消息: SQLite Error 10: 'disk I/O error'.
- 堆栈跟踪:
```
   at Microsoft.Data.Sqlite.SqliteException.ThrowExceptionForRC(Int32 rc, sqlite3 db)
   at Microsoft.Data.Sqlite.SqliteCommand.PrepareAndEnumerateStatements()+MoveNext()
   at Microsoft.Data.Sqlite.SqliteCommand.GetStatements()+MoveNext()
   at Microsoft.Data.Sqlite.SqliteDataReader.NextResult()
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader(CommandBehavior behavior)
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader()
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteNonQuery()
   at System.Data.Common.DbCommand.ExecuteNonQueryAsync(CancellationToken cancellationToken)
--- End of stack trace from previous location ---
   at MetalMonitor.Core.DatabaseService.InitializeAsync() in D:\code\MetalMonitor\MetalMonitor.core\Services\DatabaseService.cs:line 55
   at MetalMonitor.UI.App.OnStartup(StartupEventArgs e) in D:\code\MetalMonitor\MetalMonitor.UI\App.xaml.cs:line 89
   at System.Threading.Tasks.Task.<>c.<ThrowAsync>b__128_0(Object state)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)
   at System.Windows.Threading.ExceptionWrapper.TryCatchWhen(Object source, Delegate callback, Object args, Int32 numArgs, Delegate catchHandler)
```

> 【2026-09-05 人工分析 · 关联 #3b、#4】本条为旧版本全局处理器自动写入。`SQLITE_IOERR (Error 10)` 发生在 `InitializeAsync` 设置 WAL 模式阶段，时间点紧随 #3b 两次卡死进程（16:44/16:45，持有数据库句柄被强制结束）之后，判定为异常进程残留的文件句柄/-shm 共享内存导致的**瞬时文件系统错误**，而非代码缺陷：卡死进程被强杀后短时间内文件锁未释放，新进程执行需要独占锁的 `PRAGMA journal_mode=WAL` 失败。修复 #3b 后重新实测启动，数据库正常打开、主窗口正常显示，未再复现。SQLite 自身在下次打开时会对 WAL/-shm 做热恢复。若今后在可移动/网络驱动器上反复出现 Error 10，应提示用户检查数据库存储路径所在磁盘是否可用。

### 20. 修改设备后 NullReferenceException（#15 同模式复发）

- **时间**: 2026-09-09 01:05
- **文件**: `MetalMonitor.UI/ViewModels/DeviceManagementViewModel.cs`
- **函数**: `UpdateDeviceAsync`（崩溃点原第 138 行：`StatusMessage = $"设备 [{SelectedDevice.Name}] 修改成功";`）
- **异常类型**: `System.NullReferenceException`（HResult=0x80004003），Source=MetalMonitor.UI
- **现象**: 在设备管理界面选中已有设备并点"修改"后，程序中断退到 VS 调试界面
- **成因**: 与 #15 完全相同的 WPF 绑定副作用模式。`UpdateDeviceAsync` 在第 135 行 `await LoadDevicesAsync()`，而 `LoadDevicesAsync` 内部执行 `Devices.Clear()`（第 89 行）；集合清空时 DataGrid 的 `SelectedItem` 被置空，双向绑定随即把 `SelectedDevice` 写为 `null`；方法继续执行到原第 138 行访问 `SelectedDevice.Name` 即抛空引用。`AddDeviceAsync`/`DeleteDeviceAsync` 未崩溃是因为二者分别用局部变量 `device.Name` 和提前捕获的 `deviceName` 构造状态消息，唯独 `UpdateDeviceAsync` 在 reload 之后直接使用了 `SelectedDevice`
- **解决方法**:
  1. 在 reload 之前用局部变量捕获设备引用（`deviceToUpdate`）与名称（`updatedName`），后续数据库调用与状态消息均使用局部变量；
  2. reload 完成后按 `Id` 用 `Devices.FirstOrDefault` 找回重建集合中的新对象并重新赋值给 `SelectedDevice`，保持 DataGrid 选中态与编辑表单显示；
  3. 补充 `using System.Linq;`。
- **教训/最佳实践**:
  - 教训表第 13 条（#15）再次验证：**任何"先 reload 集合、后用 SelectedXxx"的代码都必须在 reload 前用局部变量捕获**；同类 ViewModel（设备管理、指标设置）应统一此模式；
  - `LoadDevicesAsync`/`LoadMetricConfigsAsync` 这类 `Clear()`+重建的方法会使绑定的选中项失效，reload 后如需保持选中态，应按 Id 重新选中。
- **最后校验**: 2026-09-09 修复后 `dotnet build` 通过（0 错误）。

---

## 八、固件与串口调试问题

### 21. 串口 printf 中文显示为乱码（编码不一致）

- **时间**: 2026-09-14（W1 第 3 天，T1.5 串口 printf 联调）
- **文件**: 野火标准库例程 `[15-2] 串口发送之发送字符串与printf` 的 `main.c`（外部例程，非 MetalMonitor 仓库内代码）
- **现象**: 串口助手收到输出，英文/数字正常，中文全部乱码
- **根本原因**: 收发两端「编码字典」不一致。野火该例程 `.c` 源文件保存为 **UTF-8** 编码，中文字符串以 UTF-8 字节序列存储在 Flash 中并原样发出；而野火串口助手在中文 Windows 下默认按 **GBK/ANSI** 解释收到的字节。英文与数字在 GBK/UTF-8 中字节完全相同，故英文正常；中文因两套字典对不上而乱码。串口本身只能传「字节流」，并无「中文」概念，中文正确显示的唯一前提是收发双方约定同一编码
- **解决方法**: 在串口助手切换「显示编码 / 字符编码」为 `UTF-8`（本次实测有效），与源文件编码对齐；通用做法是「先确认源文件是什么编码，再把助手显示编码切成一致」。长期方案是后续项目统一 UTF-8，自定义二进制协议阶段直接发纯字节/十六进制绕开编码问题
- **教训/最佳实践**: 串口调试「英文正常、中文乱码」≈ 100% 是编码不一致，与波特率无关（波特率错会连英文一起乱）；排查顺序为 波特率 → COM 口/驱动 → 源文件编码 vs 助手显示编码

### 22. 万用表测 PTC 冷态电阻读数剧烈跳动

- **时间**: 2026-09-16 21:30 左右（T1.1 到货核对，PTC 常温电阻测量）
- **对象**: PTC 黄膜加热片 5V/70℃/1.2-2W（理论冷态电阻 R=U²/P ≈ 12.5~21Ω）
- **现象**: 万用表电阻档读数极不稳定、持续跳动
- **根本原因**: ① 小阻值测量时接触电阻（0.5~2Ω）占比大，表笔与细线头接触压力变化即引起大幅跳变；② 手指捏住黄膜发热体，体温对 PTC（正温度系数元件）持续加热，电阻随温度上升而爬升；③ 自动量程档在小阻值区间反复换档刷新；④ 表笔插孔未插紧/表笔线断股（需短接自检排除）
- **解决方法**: 手动拨 200Ω 量程；表笔先短接确认 0.0~0.5Ω 稳定（基准自检）；只捏导线不碰发热体，表笔用力顶住线头金属端保持 3 秒，读初始最小值；末位 ±0.5Ω 内跳动属正常
- **教训/最佳实践**: 测小电阻先短接校零、固定接触、避免热源；PTC/NTC 类温敏元件测量时手指温度本身就是干扰源
- **追加（同日晚）**: 静置后复测仍出现 57Ω→130Ω 单调爬升。补充根因：① **仪表自热误差（self-heating）**——电阻档输出的测量电流对热容量极小的聚合物 PTC 薄膜持续加热，反复/长时间挂表测量等于断续加热，必须"快测 2 秒撤离"；② 软线头接触电阻随按压力度/氧化程度漂移。最终结论以次日晨起"冷态单次快测"为准：13~25Ω 为 1.2-2W 标称档；稳定 40Ω 上下为约 0.6W 低功率档（可接受，仅升温慢）；每次乱跳上百则为接触问题
- **教训/最佳实践（追加）**: 测热敏元件严禁长时间连续测量；读数要取"搭上瞬间的初始值"
- **手法对比验证（同日晚，用户实测）**: 表笔"抬"金属丝时接触脱离→显示 OL/无读数（开路）；把金属丝压在纸面（纸仅作绝缘垫板，不导电）上使接触稳固→读数出现但随即因持续自热而爬升。实证"接触可靠性"与"自热误差"两因素独立存在。正确手法：压实于绝缘垫板→读 1~2 秒初始值→立即撤离→两次测量间隔 ≥30s 散热
- **定论（2026-09-16 深夜）**: 结束前再测，压纸法快测读初始值 39Ω→数秒后 41Ω，仅爬 2Ω，读数可信。换算 P=U²/R=25/40≈0.6W。**结论：这片 PTC 为约 0.6W 低功率档，非下单标的 1.2-2W 档**；元件未损坏（自限温特性正常），项目可用（升温慢利于 PID 整定），无需退货。T1.1 之 PTC 核对项至此闭合，仅剩合照 + MOSFET 通断。

---

## 九、硬件选型与流程判断问题

### 23. AD4184 光耦隔离模块"接不上面包板"——AI 连续 4 次误判致项目停摆 2 天

- **时间**: 2026-09-16 ~ 2026-09-18（T1.1 硬件核对阶段；用户反馈"两天没有进度"）
- **文件**: 非代码问题，涉及 `PROJECT_PLAN.md` 的 T1.1 / T6.1 与 `PROJECT_CONTEXT.md` 硬件清单
- **现象**: 用户组装 AD4184 模块时无法确定接线与焊接位置；AI 连续 4 轮给出互相矛盾的结论，用户反复求证仍无解，导致 W1 末停摆约 2 天，并产生"要不要买镊子/洞洞板/防静电垫练焊接"的连锁焦虑
- **根本原因（三层）**:
  1. **AI 侧（主因）**：未看清用户上传照片中的丝印与孔位，即基于"通用 HW-532 模块"的假设连续给出具体接线结论（第 1 轮误判"端子块已焊好"、第 2 轮误判"背面无方形焊盘"、第 3 轮误判"焊排针即可"、第 4 轮才转向孔距分析）。**用假设替代观测**是全部错误的共同根因
  2. **元件侧（客观事实）**：该模块板孔为 **4mm 间距**（配套螺丝端子块），用户手持排针为 **2.54mm 面包板间距**；两者物理不兼容，**不存在"更聪明的插法"**
  3. **流程侧**：把一件 ¥1.38、且计划中本属 W6（T6.1）的元件，误当成 W1 的阻塞项处理；计划书虽有"退路：S8050 三极管预驱动"与"W1-W7 走面包板"的既定规则，但未被应用
- **解决方法**:
  1. 停止在 AD4184 上继续投入；判定为"物理不匹配 → 换方案"，而非"用户不会操作"
  2. `PROJECT_CONTEXT.md` 关键决策记录新增 2026-09-18 条目：**AD4184 移出关键路径，降级为 W8 焊接练习耗材**，并写明退场条件
  3. MOSFET 3.3V 通断实测回归计划本位——T6.1（W6）；W6 改用面包板方案驱动 PTC，不依赖该模块
  4. `PROJECT_PLAN.md` T1.1 标 DONE（其「验收/验证方式」列仅要求订单截图与到货逐项拍照，均已具备；此前"三件合照"为 AI 自行加严的标准，予以撤销）
  5. 终止"为焊接而买工具"的连锁决策，确立规则：**工具只在当前动作被它卡住时才买**
- **教训/最佳实践（已发生 4 次，按规则补充详述）**:
  - **触发条件**：需基于图片/丝印判断硬件型号或接法，但无法从图像中确认关键信息（丝印字母、孔距、封装）时
  - **影响范围**：单次可造成 1~3 天进度停摆 + 用户自我怀疑；本次连带引发"是否买工具"的额外决策负担
  - **解决路径（按优先级）**：① 先请用户口述关键标识，再给结论；② 若属物理不匹配（孔距/封装/电平域），直接换方案或止损，**不建议加倍练习**；③ 用计划书既有规则（退路、范围界定、时间归属）裁决，不做现场临时决策
  - **最佳实践**：
    - **物理不匹配 ≠ 技能不足**。4mm vs 2.54mm 孔距、TO-220 vs SOT-23 封装、3.3V vs 5V 电平域——这类冲突靠练手无法解决，识别后应立刻换方案
    - **成本锚定**：为一个 ¥1.38 元件消耗 2 天，说明决策时缺失"时间成本 vs 元件价值"的比较；任何阻塞项都应先问"它在计划里属于哪个任务、值多少时间"
    - **AI 的猜测必须显式标注为猜测**，并优先索取可验证信息（丝印原文、实测数据）；不得用"这类模块通常是……"的通用知识替代对具体实物的确认
- **最后校验**: 2026-09-18 `PROJECT_PLAN.md` T1.1 已标 DONE；`PROJECT_CONTEXT.md` 决策记录 / 硬件清单 / 进度指针 / 开放问题四处已同步

---

## 十、工具链与环境问题

### 24. Crc16ModbusTest 运行无任何输出——顶级语句与显式 Main 冲突（CS7022/CS8321）

- **时间**: 2026-10-01（T2.3 CRC16 脚本验证阶段）
- **文件**: `tools/Crc16ModbusTest/Program.cs`，`Main`（原 L23，已移除）
- **现象**: `dotnet run` 编译成功但程序启动即退出、控制台无任何输出；伴随警告 CS7022（入口点是全局代码，忽略 Main）与 CS8321（声明了本地函数 Main 但从未使用）
- **根本原因**: `dotnet new console` 模板默认使用**顶级语句**（Top-level Statements，C# 9+），程序入口是直接写在文件顶层的语句。在顶级语句文件中再写 `static void Main`，编译器将入口绑定到"全局代码"，显式 `Main` 被降级为**从未调用的本地函数**；而该文件顶层只有函数声明、无可执行语句，故程序空跑退出
- **解决方法**: 移除 `static void Main(string[] args)` 包装，测试代码直接作为顶级语句放在文件开头；`ComputeCrc16` 保留为顶级**局部函数**（声明须位于顶级语句之后）。修改后 `dotnet run` 输出 `"123456789" -> 0x4B37 PASS` 与空数据 `0xFFFF`，T2.3 C# 端验证通过
- **教训/最佳实践**:
  - 看清模板生成的骨架再往里填代码：`dotnet new console` 自 .NET 6 起默认顶级语句，不再有 `class Program { static void Main }` 外壳；要么全程顶级语句，要么自建完整类结构，**二者不可混写**
  - 编译器警告 CS7022/CS8321 即为直接线索：**"入口点是全局代码" = 存在顶级语句冲突**
  - 控制台程序"运行无效果"时，先怀疑入口点问题（顶级语句冲突 / 窗口一闪而过），再怀疑逻辑
- **最后校验**: 2026-10-01 `dotnet run --project tools/Crc16ModbusTest` 输出 0x4B37 PASS

### 25. Keil 链接错误 L6218E: Undefined symbol Delay_ms——函数名与野火库版本不匹配

- **时间**: 2026-10-02（T2.2 DS18B20 集成阶段）
- **文件**: 野火 `[29-2]` 工程 `User/main.c`，`main` 的 while 循环（调用处）
- **现象**: bsp_ds18b20.c 编译通过（2 个换行 warning、0 error），链接阶段报 `.\Objects\Fire_F103.axf: Error: L6218E: Undefined symbol Delay_ms (referred from main.o)`，Target not created
- **根本原因**: 该版本野火标准库 `bsp_systick.h` 只导出 `SysTick_Init / SysTick_CountPlus / SysTick_GetCount / SysTick_DelayMs`，**不存在名为 `Delay_ms` 的函数**。旧教程/网络资料中的 `Delay_ms` 是其他版本命名，直接照抄导致符号名不存在。注意链接器**只报 Delay_ms、不报 SysTick_Init**，说明 bsp_systick.c 已在工程内，属"声明/实现名字不一致"，不是"源文件未加入工程"
- **解决方法**: main.c 中 `Delay_ms(1000)` 改为 `SysTick_DelayMs(1000)`；顺手在 bsp_ds18b20.h/.c 文件末尾各加一个空行，消除 #1-D（last line ends without a newline）警告
- **教训/最佳实践**:
  - L6218E 分诊两步：① 同一文件的其他符号（如 SysTick_Init）若能解析 → .c 已参与链接，问题在**名字拼写/签名**；② 全部符号都未定义 → 才是 .c 未加入 Keil 工程组
  - 不确定函数真名时，Keil 中光标置于函数名按 **F12（Go to Definition）**，或直接翻开对应 bsp_xxx.h 核对声明，不要凭记忆/旧资料写函数名
  - 跨版本移植例程时，函数命名风格（`Delay_ms` vs `SysTick_DelayMs`、`DWT_Delay_us` 等）是最常见的不兼容点
- **最后校验**: 2026-10-02 改为 SysTick_DelayMs 后编译 0 error 并烧录成功，DS18B20 温度正常输出（见 #26）

### 26. DS18B20 串口启动瞬间单次乱码——复位垃圾字节 + 中文 GBK 编码

- **时间**: 2026-10-02（T2.2 验收烧录后）
- **文件**: 野火工程 `User/main.c`，启动 `printf("\r\n ----ds18b20传感器实验开始---\r\n")`
- **现象**: 烧录后串口第一行出现 `J???o?` 类乱码，但其后全部温度输出（30.69→33.06℃）完全正常
- **根本原因**: ① MCU 复位瞬间 USART/GPIO 时钟未稳定，线路抖出无效字节，串口助手如实显示（无害）；② 启动 printf 含中文，Keil 源文件按 GBK 编码发送，串口助手编码不一致即花屏。**判据**：波特率错误会全程乱码，仅开头一次乱码说明波特率正确，问题仅限启动瞬间/中文字符串
- **解决方法**: 启动 printf 改为纯英文（串口调试与二进制协议一律只发 ASCII/数字）；剩余偶发首字节为复位噪声，不处理
- **教训/最佳实践**:
  - "全程乱码查波特率/时钟，仅开头乱码查复位噪声和字符串编码"——用乱码的**范围**定位，而不是一见乱码就怀疑硬件
  - 串口协议帧（T2.4）本身是二进制，绝不依赖可打印中文，从源头规避编码问题
- **最后校验**: 2026-10-02 温度数据流稳定，手捏升温/松开回落趋势正确，T2.2 DONE

### 27. 误判 Delay_us(750000) 存在 8 倍时钟错误——未核对 SysTick 时钟源的臆测（关联 #23）

- **时间**: 2026-10-04（DS18B20 非阻塞重构讨论）
- **文件**: 野火工程 `User/ds18b20/bsp_ds18b20.c` Delay_us、`User/systick/bsp_systick.c` SysTick_Init
- **现象**: AI 一度断言"标准库 SysTick_Init 通常配 HCLK_Div8（9MHz），Delay_us 按 72MHz 数拍会导致 750ms 实际等 6 秒"。用户实测串口温度间隔约 1 秒，与断言矛盾
- **根本原因**: 该工程 `SysTick_Init` 调用的是 `SysTick_Config(SystemCoreClock/1000)`，而 CMSIS 的 SysTick_Config 默认选择 **HCLK（72MHz，不分频）** 作为 SysTick 时钟源，代码中也没有 `SysTick_CLKSource_HCLK_Div8` 配置。因此 Delay_us 按 72 拍/us 计数是**正确**的，750ms 就是 750ms。AI 把"某些版本工程常见 /8 配置"当成了本工程的事实，属于未读代码先下结论
- **解决方法**: 无需改代码。Delay_us 的回绕累加算法（uint32 tcnt 差值累加）与时钟频率均正确；重构非阻塞的动机回归为"避免 750ms 阻塞饿死主循环"，而非"修正定时错误"
- **教训/最佳实践**:
  - 时钟树结论必须落到三处实证：① SysTick_Config/CLKSource 配置；② CTRL 寄存器 CLKSOURCE 位；③ 实测输出周期（数 10 秒内打印行数）。"通常是……"不构成证据
  - 用户的现场观测（约 1 秒间隔）是有效数据，与推断冲突时先怀疑推断
- **最后校验**: 2026-10-04 已读 bsp_systick.c L40 确认 SysTick_Config(SystemCoreClock/1000)，无 /8 配置，结论销案

### 28. L6218E Undefined symbol temperature_task——变量定义与 extern 声明大小写不一致（关联 #25）

- **时间**: 2026-10-04（DS18B20 非阻塞任务重构）
- **文件**: `App/temperature/app_temperature.c` L6 等、`app_temperature.h` L15、`User/stm32f10x_it.c`
- **现象**: app_temperature.c 已确认加入 Keil APP 组、Include Path 与 main.h 包含均正确，链接仍报 `Undefined symbol temperature_task (referred from stm32f10x_it.o)`
- **根本原因**: **C 语言大小写敏感**。头文件 extern 声明为 `temperature_task`（小写 t），.c 中实体却定义为 `Temperature_task`（大写 T），同文件内部引用也大小写混用。编译器把头文件声明当"欠条"放行，链接器找不到小写名字的实体——大写定义与小写声明是两个无关符号
- **解决方法**: 全工程统一为小写 `temperature_task`（.c 中 replace_all），与头文件/中断文件一致；函数名 `Temperature_TaskXxx` 保持大写 T 不动（变量与函数命名风格本就不同）
- **教训/最佳实践**:
  - L6218E 分诊补第三条：**.c 已在工程内、include 也正确仍 undefined → 核对定义处与声明处的拼写与大小写**，C 中 `Foo` 与 `foo` 是两个符号
  - 复制现有模板（app_potentiometer）改名时，变量名全局替换最易漏大小写；改完先编译再写业务逻辑
  - 结构体标签用大驼峰（Temperature_TaskInfo）、实例变量用小写下划线（temperature_task）是本工程既有风格，保持一致即可让此类错误肉眼可辨
- **最后校验**: 2026-10-04 统一大小写后待用户 Rebuild 验证

### 29. Device.Temperature 特判是迁移遗留的死写入（零读取）

- **时间**: 2026-10-07（T3.2 前置代码走读时发现）
- **文件**: `MetalMonitor.core/Services/DeviceService.cs`（删除前 L253-254、L310-314）、`MetalMonitor.core/Device.cs`（删除前 L21、L70-74、构造函数 initialTemp 参数与两处赋值）
- **现象**: 两条数据通路都有 `if (metricName == "Temperature") device.Temperature = value;` 硬编码特判，注释写"兼容旧的 Temperature 主属性"
- **根本原因**: 架构从"单一温度属性 `Device.Temperature`"演进到"通用指标字典 `_metricValues` + SetMetricValue"后，UI 改为绑定字典的 "Temperature" 键，但旧属性、旧字段、两处特判赋值、构造函数的 initialTemp 参数未随重构删除。grep 证实：全项目仅 2 处**写** `.Temperature`，**零处读取**，XAML 也无 `Temperature` 绑定——写进 `_temperature` 的值无人消费，是纯死代码（dead write）。特判本身还有第二个问题：硬编码指标名字符串，新增同类特殊指标就会繁殖 if
- **解决方法**: 删除两条特判赋值、`Temperature` 属性、`_temperature` 字段、构造函数 `initialTemp` 参数（`Device(string name, double initialTemp=60)` → `Device(string name)`，测试的单参数调用保持兼容）及两处构造函数内赋值；保留真正在用的字典通路
- **验证**: `dotnet build` 0 错误；`dotnet test` 48/48 通过
- **教训/最佳实践**:
  - 识别死代码的证据链：分别 grep 写入点、读取点（.cs 的 getter 调用）、XAML 绑定点；只写不读即为死写入，可安全删除
  - 看到"兼容旧的 xxx"注释要核实兼容对象是否还在——迁移标记容易成为永久残留
  - 通用化重构（单属性→字典）完成后，必须回头删旧路径，否则两套模型并存增加理解成本（本次正是用户走读时被绊住）
- **最后校验**: 2026-10-07 删除后 build 0 error / 48 tests passed

### 30. MQTT 通路未校验 IsActive + Core 层 await 缺 ConfigureAwait(false) 致 Dispose 死锁风险

- **时间**: 2026-10-07（走读 DeviceService 数据流时由代码审查发现并修复）
- **文件**:
  - `MetalMonitor.core/Services/DeviceService.cs`：`UpdateDeviceFromMqtt`（L240-245）、全部 `await` 调用点
  - `MetalMonitor.core/Services/DatabaseService.cs`：全部 `await` 调用点（共 50 处）
- **现象/问题一**：模拟数据通路 `UpdateDeviceData` 有 `if (!device.IsActive) continue;`，但 MQTT 通路 `UpdateDeviceFromMqtt` 找到设备后直接更新——用户在 UI 禁用某设备后，MQTT 推来的数据仍会改 UI、触发报警、写库，业务行为不一致
- **问题一解决方法**：在 `UpdateDeviceFromMqtt` 找到 device 后、进入指标循环前加 `if (!device.IsActive) return;`，与模拟通路对齐
- **现象/问题二**：`Dispose()` 用 `FlushAsync().GetAwaiter().GetResult()` 同步阻塞。Dispose 由 App 关闭在 UI 线程触发，而 `FlushAsync → WriteBatchAsync → db.AddDataRecordsBatchAsync` 内部 `await` 均未加 `ConfigureAwait(false)`，延续默认调度回 UI 同步上下文；UI 线程正被 `GetResult()` 阻塞，形成经典"同步阻塞+await 回 UI 上下文"死锁（当前碰巧不死锁，因为 `Microsoft.Data.Sqlite` 异步方法多为同步完成返回已完成 Task，但属于依赖底层实现细节的脆弱设计）
- **问题二解决方法**：给 DeviceService 与 DatabaseService 中**所有** `await` 追加 `.ConfigureAwait(false)`——Core 层不直接操作 UI，无需回原始同步上下文，从根源消除 Dispose 死锁可能
- **验证**: `dotnet build` 0 错误；`dotnet test` 48/48 通过
- **教训/最佳实践**:
  - 多通路并行（模拟/MQTT/串口）时，业务规则（如 IsActive 过滤）必须集中或逐条对齐，否则通路间行为漂移
  - 非 UI 层（Core/Services）的 `await` 默认加 `ConfigureAwait(false)` 是 .NET 库代码的硬规则——既避免不必要的上下文切换开销，更防止被上层同步阻塞（`.Result`/`.GetAwaiter().GetResult()`）时死锁
  - 代码审查意见要分级：真实漏洞（IsActive）、设计缺陷（ConfigureAwait）必改；量级可忽略的（_lastSimValues 泄漏）、依赖前提不成立的（Name vs Id 需先确认消息语义）暂缓
- **最后校验**: 2026-10-07 修复后 build 0 error / 48 tests passed

### 31. FrameParser 属性名大小写不一致致 CS1061 编译失败

- **时间**: 2026-10-12（T3.3 FrameParser 编写过程中即时发现）
- **文件**: `MetalMonitor.core/Services/FrameParser.cs`：`SensorDataFrame` 类（L249-261）、`ProcessFrame` 方法（L191-193）
- **现象**: `dotnet test` 报 CS1061："SensorDataFrame"未包含"Ds18B20Online"的定义。FrameParser.cs 内属性定义为 `Ds18b20Online`（18 中 b 小写），FrameParserTests.cs 引用为 `Ds18B20Online`（B 大写），C# 大小写敏感导致编译失败
- **根因**: DS18B20 是芯片型号，缩写词中的 B 应大写。初始编码时未遵循 C# 命名规范（缩写词全大写），测试文件按规范写，两处不一致
- **解决方法**: 统一为 `Ds18B20Online`（B 大写），涉及 3 处：属性定义、构造函数参数名、局部变量名
- **验证**: 修复后 `dotnet test` 54/54 通过（含 6 个新增 FrameParser 测试）
- **教训**: C# 命名规范中缩写词（如 DS18B20、ADC、PWM）应全大写，属性名中只首字母大写其余大写保留（如 `Ds18B20Online`）。写代码时先确定命名再写实现，避免同一名词两处大小写不一致
- **最后校验**: 2026-10-12 修复后 build 0 error / 54 tests passed

### 32. FrameParser 未校验 LEN 字段上下界，假帧头可致 _frameBuffer 越界与真帧吞噬

- **时间**: 2026-10-14（T3.3 代码走读时由用户提问触发分析，方案 A 修复）
- **文件**: `MetalMonitor.core/Services/FrameParser.cs`：`TryParseFrames` 的 `ReadingLen` 分支（登记时 L142-158；2026-10-14 因 NODE_ID 状态插入复核为 L151-167）；常量定义 L41-52
- **现象（潜在缺陷，未在运行中触发）**: ReadingLen 分支接受 LEN=0~255 的任意值，但帧组装缓冲 `_frameBuffer` 仅 32 字节（帧总长=LEN+5，安全上界 LEN≤27）。噪声字节流中每 65536 个位置可能偶然形成假帧头 `AA 55`：
  - **上界问题**：假 LEN>27 时状态机持续向 `_frameBuffer` 写入，到 `[32]` 抛 IndexOutOfRangeException（后台线程未兜底则崩进程）；同时假帧会把紧跟其后的真帧头 `AA 55` 当载荷吞噬
  - **下界问题**：LEN=0 时帧长=5，ReadingBody 的转态条件 `_framePos == expectedFrameLength-2(=3)` 初始即满足但永远要在自增后才判断，状态机滞留 ReadingBody 持续写入直至越界
- **根因**: 状态机接受的输入域（LEN ∈ [0,255]）大于下游缓冲的容量域（LEN ≤ 27），两个组件对"合法帧"的定义不一致；协议字段在"信任长度前先验证长度"这一协议解析基本防御动作缺失
- **解决方法（方案 A）**:
  1. 提取常量 `FrameBufferSize=32`、`MinLen=1`、`MaxLen=FrameBufferSize-5=27`，上界从缓冲容量推导而非写死魔数
  2. ReadingLen 分支在接受 LEN 前校验 `if (b < MinLen || b > MaxLen)`：非法则 BadFrameCount++、`_totalRead--` 回退当前字节（该字节可能恰为真帧头的 0xAA，不能丢）、状态机退回 WaitingHead 重新扫描
  3. BadFrameCount 语义注释补充"LEN 非法"
- **验证**: 新增 4 个单测（LEN 超上限恢复 / LEN=0 拒绝 / 假 LEN 字节恰为真帧头 AA 的回退 / 超大 LEN+40 字节垃圾不越界回归）；`dotnet test` 58/58 通过
- **教训/最佳实践**:
  - 长度字段必须做**上下界双侧校验**，且上界必须从真实缓冲容量推导（魔数 24/27 会随缓冲调整腐化）
  - 拒绝非法字节时考虑"回退 1 字节重扫"而非直接丢弃——`AA 55 AA 55 ...` 场景中被拒字节可能就是真帧头
  - 协议解析器不能信任线路上的任何字节：帧头可由噪声偶然形成，长度字段必须先验证再使用（Modbus/TCP 拆包/CVE 类漏洞的共同根源）
  - 关联经验：Modbus_Data_Check 类解析器的 nLen 未校验会导致 CRC 越界读、payload 越界写，本缺陷同构
- **最后校验**: 2026-10-14 修复后 build 0 error / 58 tests passed

### 33. T3.4 改帧引入 ReadingNodeId 状态后转移未重接 + 帧缓冲下标错位（三处同源）

- **时间**: 2026-10-14（T3.4 第②步：用户先行手动改造 FrameParser 至 12 字节帧，AI 代码复查发现）
- **文件**: `MetalMonitor.core/Services/FrameParser.cs`：`WaitingHead2` 分支（L121-141）、`ReadingLen` 分支（L151-167）、`ProcessFrame` 的 NODE_ID 读取（L212）
- **现象（潜在缺陷，编译通过、联调必现）**: 帧格式 11→12 字节（PROTOCOL v0.9 修订2 加 NODE_ID）后：
  1. `WaitingHead2` 收到 0x55 后仍转 `ReadingLen`（未改指新建的 `ReadingNodeId`）→ 新状态成死代码，NODE_ID 字节被 ReadingLen 当 LEN 校验；当前固件 NODE_ID=0x00 < MinLen=1，**每个真帧都被判非法拒绝**（BadFrameCount 每帧 +1、零解析）
  2. `ReadingLen` 把 LEN 写入 `_frameBuffer[2]`（新布局 [2] 是 NODE_ID，LEN 应在 [3]）
  3. `ProcessFrame` 从 `_frameBuffer[3]` 读 NODE_ID（[3] 是 LEN，应在 [2]）
- **根因**: 向既有状态机**插入新状态**时，只加了枚举成员和状态分支，没有重定向所有指向"原后继状态"的转移——枚举加成员不会让旧转移自动绕路；帧布局整体后移 1 字节时，写路径（LEN 下标）与读路径（NODE_ID 下标）各自改了一半，写入侧漏改、读取侧多改一位
- **解决方法**: ①WaitingHead2 转向 `ReadingNodeId`；②ReadingLen 写 `_frameBuffer[3]`；③ProcessFrame 读 `_frameBuffer[2]`；同步 6 处事实性注释（状态机列表 / 协议参考 12 字节 LEN+6 / 预期帧长公式等）
- **验证**: 10 个旧用例向量重算为 12 字节（CRC 覆盖 8 字节：`00 06 01 FD 00 E2 0C 01` → 0xF82E）+ 新增 NODE_ID=0x2A 提取用例（CRC 0x9FAC，帧 `AA 55 2A 06 01 FD 00 E2 0C 01 AC 9F`，验证解析器"只提取不判权责"的机制/策略分离）；`dotnet test` 59/59 通过
- **教训/最佳实践**:
  - 状态机插入状态是结构性变更：枚举成员 + 状态分支 + **所有入转移**三件套缺一不可；编译器不报"不可达状态"，全路径单测是唯一防线
  - 布局整体偏移时，"写入下标"与"读取下标"必须按同一张偏移表逐字段对位核对，不能各自凭记忆 +1/-1
  - CRC 覆盖公式 `_expectedFrameLength - 4` 从偏移 2 起算，NODE_ID 恰好落在偏移 2，CRC 代码零改动即兼容——**新字段插在既有公式的边界上可显著压缩改动面**
- **关联**: #32（LEN 上下界校验——本次校验逻辑原样保留仅下标修正，MaxLen 随帧格式 27→26）
- **最后校验**: 2026-10-14 修复后 build 0 error / 59 tests passed

### 34. 枚举类型名被同名属性遮蔽（成员查找优先于类型查找，CS1061）

- **时间**: 2026-10-14（T3.4 第③步 D1 抽枚举，重构后首次 `dotnet test` 编译期暴露）
- **文件**: `MetalMonitor.core/Services/DeviceService.cs`（using 别名区文件头 / `_selectedDataSource` 字段声明 / `SetDataSourceAsync` 的 switch 与三处回退点）——**行号随迭代变动，按字段与方法名定位**（2026-10-19 复核：登记时的 L52/L163~L262 已因后续注释与 D3 改动位移）
- **现象**: 新增 `enum DataSource` 后，类内 `DataSource.Simulation` 报 error CS1061"**string** 未包含 Simulation 的定义"——编译器把 `DataSource` 解析成了本类的 `public string DataSource` 属性（IDeviceService 对外契约），而非新枚举类型
- **根本原因**: C# 简单名称解析顺序 = 局部变量/参数 → **类成员** → 命名空间类型；枚举与既有属性同名时，类内永远先命中属性，枚举类型在该类内部不可直接达（类外部如 Tests 项目无同名成员，不受影响）
- **解决方案**: 文件头加 `using DataSourceKind = MetalMonitor.Core.DataSource;`，类内枚举一律写作 `DataSourceKind`。三方案取舍：别名（最小侵入，选此）／枚举改名（零别名但偏离计划文档命名）／属性改名（破坏接口与 ViewModel，波及面最大）
- **教训/最佳实践**: 向类内引入新类型名前，先 grep 该类是否已有同名成员（属性/方法/嵌套类）；.NET 里 `DataSource`/`Timer`/`Task`/`Path` 都是常见撞名雷区。撞名不改语义的前提下，using 别名是单文件局部的标准解
- **关联**: #33（同为 T3.4 改造期缺陷；本条为编译期拦截、未入运行时）
- **最后校验**: 2026-10-14 修复后 dotnet test 68/68 通过

### 35. 测试误断言：忽略字典键归一后的碰撞（"2A" 与 "2a" 是同一个字节键）

- **时间**: 2026-10-14（T3.4 第④步，新增路由表单测首轮 dotnet test 即红）
- **文件**: `MetalMonitor.Tests/ConfigServiceTests.cs`：`NodeRouting_HexKeysParsed_InvalidKeysIgnored`
- **现象**: 断言 `routing.Count == 2` 实际为 1——测试 JSON 放了 `"2A"` 与 `"2a"` 两个键，本意当两个条目
- **根本原因**: 两者经十六进制解析后**归一为同一字节键 0x2A**，字典按字节键去重只剩一条；写断言时在"JSON 键空间"思考而消费方工作在"字节键空间"，两层粒度混用
- **解决方法**: 重写用例改用两个不同节点（`"2A"`→0x2A、`"0f"`→0x0F），顺带以小写键覆盖大小写不敏感解析
- **教训**: 给"解析归一"逻辑写测试时，断言必须工作在归一后的键空间；凡有键归一（大小写/编码/单位换算），先问"这两个输入会撞成一个键吗"
- **关联**: #33/#34（同为 T3.4 期间缺陷，均为当轮发现当轮修复）
- **最后校验**: 2026-10-14 修复后 dotnet test 72/72 通过

### 36. 管线重构破坏了既有测试的隐式契约（MQTT 用例默认"接入即写值"）

- **时间**: 2026-10-19（T3.4 第⑤步：DeviceService 改为"接入事件驱动 + 派发节拍驱动"后，首轮 dotnet test 即红）
- **文件**: `MetalMonitor.Tests/DeviceServiceBufferTests.cs`：MQTT 缓冲相关用例（断言"收到消息即从 device 读到指标值"）
- **现象**: 重构后这些用例失败——不再是"数据不对"，而是**设备上压根没有值**（`GetMetricValue` 返回空）
- **根本原因**: 用例编写时默认了旧架构的**隐式契约**——"MQTT 消息一到就同步写入设备"。新架构把设备写值推迟到 1Hz 心跳排空暂存区（这正是本次重构的目的：把所有设备写值与 UI 事件收敛到心跳单线程）。用例仍只喂消息、不推进心跳，于是永远读不到值。**契约变了，但契约只存在于测试的隐含假设里，没有写在任何文档或接口上**
- **解决方法**: 在用例内补两步显式前置：`await SetDataSourceAsync("MQTT")`（让 MQTT 通路被点亮）+ 手动推进一次 `UpdateDevicesAsync()`（心跳派发）；配套让 mock `IsConnected` 返回 true。原有落库断言（缓冲条数/内容）不动
- **教训/最佳实践**:
  - **驱动模型变更会静默失效一批测试**：症状是"值不存在"而非"值错误"，容易被误判为业务 bug。凡改动"何时写值"，必先 grep 所有断言"读取即得值"的用例
  - 测试里对架构的**时序假设应当显式化**（写清"需先选数据源再推进心跳"），否则架构一动，用例的失败原因会指向错误的排查方向
  - 变通提示：文档与接口都能保持不变而下层时序剧变，是"双轨/暂存"类重构的典型特征——**契约不只写在签名上，也写在使用顺序里**
- **关联**: #33（状态机转移重接）/ #34（类型名遮蔽）——同为 T3.4 改造期缺陷；本条是三者中唯一"测试先红、暴露设计契约已变"的良性信号（另两条若漏检则运行时静默失效）
- **最后校验**: 2026-10-19 修复后 dotnet test 79/79 通过

### 37. 有界集合裁剪后索引滞后（Add 传入的 index 是裁剪前的 Count）

- **时间**: 2026-10-23 00:03:08（STEP-4 首次 `dotnet test` 即红）
- **文件**: `MetalMonitor.core/Collections/BoundedObservableCollection.cs`：`InsertItem`（行号随迭代变动，按方法名定位）
- **现象**: 用例 `Add_BeyondCapacity_KeepsNewestItems` 抛 `ArgumentOutOfRangeException: Index must be within the bounds of the List. (Parameter 'index')`，84 个用例中 1 个失败
- **根本原因**: `ObservableCollection<T>.Add` 的实现是 `InsertItem(Count, item)`——index 在进入重写方法时已按**裁剪前**的 Count 算好。方法内先 `RemoveItem(0)` 使 Count 减 1，随后仍用旧 index 调用 `base.InsertItem`，索引越界
- **解决方法**: 记录本次移除条数 `removed`，裁剪后执行 `index = Math.Max(0, index - removed)` 再插入；3 个单测全绿，84/84
- **教训/最佳实践**: 在"先删后插"的复合操作里，任何**先计算、后变更**的索引都会失效——索引是位置快照，不是稳定引用。此缺陷由测试先行暴露，从写错到定位不到一分钟，是"先写断言再写实现"的直接收益
- **关联**: #35（同为测试先行暴露的索引/键空间类错误）
- **最后校验**: 2026-10-23 修复后 dotnet test 84/84 通过

### 38. Dispose 冲刷后到置位之间的数据丢失窗口 + MQTT 事件未退订

- **时间**: 2026-10-24（STEP-7，外部评审提出后经代码取证确认）
- **文件**: `MetalMonitor.core/Services/DeviceService.cs`：`Dispose` / `FlushAsync` / `EnqueueDataRecord`（行号随迭代变动，按方法名定位）；`MetalMonitor.UI/App.xaml.cs`：启动调用点（改名同步）
- **现象**（静态缺陷，非运行时崩溃）:
  1. `Dispose` 先 `FlushAsync()` 冲刷、最后才 `_disposed = true`；两者之间后台线程仍可通过 `EnqueueDataRecord`（其门卫查 `_disposed`）入队，新记录随对象回收静默丢失
  2. `Dispose` 只退订了串口 `DataReceived` / `FrameParsed`，构造函数里订阅的 `MessageReceived` / `Disconnected` 从未退订，MQTT 服务持续持有本对象委托，回调可打到已释放对象且本对象无法被 GC 回收
- **根本原因**:
  - **顺序错误**：清理动作（冲刷）与阻断动作（置位）顺序颠倒。正确语义应是"停止接收 → 排空存量 → 释放引用"
  - **门卫互斥**：`FlushAsync` 自身第一行就查 `_disposed`，因此"把置位提前"这种直觉修法会让冲刷被自己的门卫挡住，反而丢掉整批缓冲——**必须把带门卫的公开入口与不带门卫的冲刷内核拆开**
  - **平台迁移风险**：`_disposed` 由 UI 线程写、MQTT/串口后台线程读，无 `volatile` 时可见性无保证（x86 强内存模型下少见于事故，ARM 弱内存模型下会真实暴露）
  - **事件泄漏**：订阅与退订不在同一处维护，改为"按能力成对声明退订"
- **解决方法**:
  1. `_disposed` 加 `volatile`（只保证可见性，不替代锁）
  2. `FlushAsync` 拆为公开门卫（只查 `_disposed`）+ 私有 `FlushCoreAsync`（只查 `_databaseService == null`，不看释放标记）
  3. `Dispose` 顺序改为：`_disposed = true`（阻断所有入队路径）→ `FlushCoreAsync` 兜底排空 → 成对退订 MQTT 两个事件 + 串口两个事件
  4. `InitializeMqttAsync` 改名 `InitializeDataSourceAsync`（其首段处理串口，名实不符），同步更新 `App.xaml.cs` 调用点与注释
- **关联**: #2（事件订阅未取消，同类第 2 次，已在 #2 下登记复核）/ #30（Dispose 死锁风险，同属生命周期路径）/ #36（管线重构改变时序契约）
- **教训/最佳实践**: 凡"有缓冲 + 有后台线程"的服务，关闭流程一律遵循**两阶段关闭**：先立旗标阻断入队，再排空存量，最后退订/释放；凡是门卫，只放在公开入口，内核必须能绕过它。验证手段是既有用例 `Dispose_FlushesRemainingBuffer`（覆盖"置位后仍能冲刷"这一关键分支）
- **最后校验**: 2026-10-24 修复后 `dotnet test` 84/84 通过，`dotnet build` 无 CS 警告（仅存量的 NU1701 包兼容提示）


