# PROGRESS

- STEP-21｜2026-10-30｜把 STEP-18/19/20 改动点的注释换成"人话版"（含 ConfigService 的 KnownPorts/RecordSerialPortSuccess、连接验证窗口、候选三层顺序、门铃边沿语义），仅注释无逻辑改动，build 0 错 + 123/123 通过｜状态：完成
- STEP-22｜2026-11-07｜窗口丙卡死（STEP-22 方案 A）：TryStartReconnect 理由二放宽为「SerialPortAppeared() || 有任意串口在场」，新增窗口丙单测，error_fix_log #42，build 0 错 + 124/124 通过｜状态：完成
- STEP-23｜2026-11-07｜注释规范化收尾：去除 D3 等决策编号、比喻与重复行尾注释，串口识别/构造函数/重连决策改为客观表述并保留，覆盖 DeviceService 等 8 个源码文件，build 0 错 + 124/124 通过｜状态：完成
- STEP-24｜2026-11-07｜死代码清理（方案 A）：删 IDeviceService.ShouldAlarm/GetAlarmThreshold facade、IDatabaseService.GetDeviceIdsAsync/GetMetricNamesAsync/GetDataRecordsAsync(无时间范围) 及实现，改写 DatabaseServiceTests 用时间范围版；RefreshInterval 接线到 MainViewModel 定时器（>0 兜底），build 0 错 + 124/124 通过｜状态：完成
- STEP-25｜2026-11-07｜状态语义扩展（后被 STEP-26 修正）：DataSourceState 加 Simulated，LinkHealth 曾误加 Connecting｜状态：完成
- STEP-26｜2026-11-08｜状态分层修正：DataSourceState 五态（Online/Faulted/Unavailable/Simulated/Searching），LinkHealth 七态（Handshaking=打开端口等首帧，Recovering=已确认设备）；TryConnectVerifiedAsync 验证窗口改 50ms 步进轮询，build 0 错 + 124/124 通过｜状态：完成
- STEP-27｜2026-11-08｜拔线无限 Searching 修复：ReconnectLoopAsync 无串口分支计入失败轮数，连续两轮全失败→Faulted 退出；真机复验通过（拔线约 5 秒内落 Faulted）｜状态：完成
- STEP-28｜2026-11-08｜Faulted 死锁修复（L1）：解除判据从 SerialPortNewlyAppeared()（基线每拍被刷新失效+陌生口不在记忆）改为「系统有任意串口即放行」，真机换口复验通过，build 0 错 + 124/124 通过｜状态：完成
- STEP-29｜2026-11-09｜W4 闸门收口：T4.2 落库/报警真机通过（DB 19.1MB，最后写入 15:25:39）；T4.3 10 分钟零崩溃（用户观察，日志旁证缺失；当日 logs/ 无新文件）；换口证据 config.json COM6 置顶；闸门评审结论写入 PROJECT_PLAN §C2，M2-b 达成且不触发 A6 降级；回写 PROJECT_PLAN/PROJECT_CONTEXT｜状态：完成
- STEP-30｜2026-11-09｜修复测试工程编译失败（L1）：ReconnectBackoffTests 补回 `using MetalMonitor.Core;`（f345521 清理 using 误删，5 × CS0103），error_fix_log #43，build 0 错 + 124/124 通过｜状态：完成
- STEP-32｜2026-11-11｜历史查询选结束日期查不到当天数据（L1）：LoadHistoricalDataAsync 把 EndDate（日历选出=当天 00:00:00）补到当天 23:59:59、StartDate 取 .Date，error_fix_log #45，build 0 错 + 124/124 通过｜状态：完成（真机待验）
- STEP-31｜2026-11-10｜详情页日期选择器点不开日历（L1）：PART_Button 由 ToggleButton 改 Button（WPF 用 `as Button` 取部件，兄弟类型转换得 null 致点击未订阅）+ 内联深色模板 + 删除被 `_popUp.Child` 覆盖的死代码 Calendar，error_fix_log #44，build 0 错 + 124/124 通过｜状态：完成（真机点击待验）
