# ARCHITECTURE

## 模块清单（一句话职责）
- MetalMonitor.UI：WPF 界面层，App.xaml.cs 持有 DI 注册表
- MetalMonitor.core：业务与数据层（DeviceService 门面 / DatabaseService 落盘 / MqttService、SerialService 数据源 / FrameParser+FrameRouter 协议栈 / ConfigService 配置【DI 单例】）
- MetalMonitor.Tests：xUnit + Moq，经 InternalsVisibleTo 访问 internal 测试构造函数

## 依赖方向
UI → core（接口）；core 内部 DeviceService 依赖各服务接口 + ConfigService 单例；Tests → core

## 数据源状态出口（STEP-3 起）
DeviceService.SetDataSourceAsync / InitializeMqttAsync → State（Online/Faulted/Unavailable）→ DataSourceStateChanged 事件 → MainViewModel 写 AlarmLogs（封送 UI 线程）。用户所选数据源与 config.json 不再被故障改写；心跳仅在所选数据源为"模拟数据"时生成模拟值（D-4/D-5）。

## 典型数据流（串口通路）
SerialPort 字节 → SerialService.DataReceived → FrameParser（环形缓冲+状态机+CRC）→ FrameRouter（NODE_ID 路由）→ DeviceService 暂存区（latest-wins）→ 心跳 1Hz 派发 → Device 事件 → UI 卡片/曲线；同步追加落库缓冲（50 条批量写 SQLite WAL）

## 技术栈选型理由
- WPF + MVVM：工控上位机桌面场景
- SQLite WAL：单机落盘、读写并发（D 见 PROJECT_CONTEXT 技术债表）
- Microsoft.Extensions.DI：换数据库等模块化需求（用户初衷：SQLite→MySQL 可替换）
