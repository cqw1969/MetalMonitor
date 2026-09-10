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


