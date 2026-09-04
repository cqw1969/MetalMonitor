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
