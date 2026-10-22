# PROGRESS

- STEP-1｜2026-10-21｜ConfigService 注册 DI 单例 + DeviceService 构造函数族收敛为 public 6 参 / internal 测试版｜状态：完成
- STEP-2｜2026-10-21｜ISerialService 增加 IsAvailable 能力维度 + ConnectAsync 端口预检（方案 A 动态计算，D-3）｜状态：完成
- STEP-3｜2026-10-22｜数据源三态状态面 + 取消失败回退（含配置改写）+ 模拟生成条件收紧 + UI 事件提示（方案 A，D-4/D-5）｜状态：完成
- STEP-3.1｜2026-10-22｜删除 _useMqtt/_useSerial 死字段及全部 14 处死写入，CS 警告归零（OnMqttDisconnected 留空挂点归 T4.1）｜状态：完成
- STEP-4｜2026-10-22｜AlarmLogs 环形保留（技术债候选，D-6 待决：容量与实现方式）｜状态：待确认
