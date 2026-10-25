# PROGRESS

- STEP-1｜2026-10-21｜ConfigService 注册 DI 单例 + DeviceService 构造函数族收敛为 public 6 参 / internal 测试版｜状态：完成
- STEP-2｜2026-10-21｜ISerialService 增加 IsAvailable 能力维度 + ConnectAsync 端口预检（方案 A 动态计算，D-3）｜状态：完成
- STEP-3｜2026-10-22｜数据源三态状态面 + 取消失败回退（含配置改写）+ 模拟生成条件收紧 + UI 事件提示（方案 A，D-4/D-5）｜状态：完成
- STEP-3.1｜2026-10-22｜删除 _useMqtt/_useSerial 死字段及全部 14 处死写入，CS 警告归零（OnMqttDisconnected 留空挂点归 T4.1）｜状态：完成
- STEP-4｜2026-10-23｜AlarmLogs 环形保留：BoundedObservableCollection 置 core/Collections，容量 200，3 单测（方案 B，D-6）｜状态：完成
- STEP-5｜2026-10-22｜UI 层错误/警告消息落盘（候选：现有 6 处 AlarmLogs 写入未接日志文件）｜状态：待确认
- STEP-6｜2026-10-22｜sealed 约定落地：4 个 EventArgs 补 sealed 消除同族不一致（D-7）｜状态：完成
- STEP-7｜2026-10-24｜Dispose 改两阶段关闭（先置位/后内核冲刷）+ 补退订 MQTT 事件 + _disposed 加 volatile + InitializeMqttAsync 改名 InitializeDataSourceAsync｜状态：完成
- STEP-8｜2026-10-24｜T4.1 设计草案：LinkHealth 双时间戳判据 + 每指标质量码 + 两行 UI + 端口名优先重连（D-8~D-11）｜状态：完成
- STEP-9｜2026-10-24｜T4.1 Core 侧：LinkHealth 五态 + MetricQuality 三态 + 两配置键 + 25 条测试（109/109）｜状态：完成
- STEP-10｜2026-10-25｜T4.1 UI 侧：顶栏数据源/链路两行状态 + 卡片质量角标与数值染色；IDeviceService 补 LinkHealth 成员（方案 B，109/109）｜状态：完成

- STEP-2｜2026-10-23｜用 .NET 8 OpenFolderDialog 替换 351 行 IFileOpenDialog COM 声明｜状态：完成
