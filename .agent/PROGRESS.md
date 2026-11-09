# PROGRESS

- STEP-15｜2026-10-28｜详情页指标下拉框空白：RefreshConfigsAsync 补发 SelectedMetricName 通知（L1，error_fix_log #39）｜状态：完成（后被 STEP-17 根治取代）
- STEP-16｜2026-10-28｜运行期断线数据源恒"正常"：MaybeStartReconnect 补 SetState(Faulted)（L1，error_fix_log #40）｜状态：完成
- STEP-17｜2026-10-29｜详情页下拉框空白根治（D-16 方案 B）：SyncAvailableMetrics 增量同步替换两处 Clear+Add，保留控件选中态，123/123 + 真机三场景复验通过｜状态：完成
- STEP-18｜2026-10-29｜重连可唤醒 + 端口重现判据（D-17）：TryStartReconnect / SerialPortAppeared / WakeReconnectLoop，build 0 错 + 123/123 通过｜状态：完成（真机插拔待验）
- STEP-19｜2026-10-29｜端口漂移（D-18 D-全）：KnownPorts 候选序列 + 连接后 3 秒验证窗口，ConfigService/config.json/DeviceService 三文件，build 0 错 + 123/123 通过｜状态：完成（真机换口待验）
- STEP-20｜2026-10-30｜补洞：候选为空时遍历全部可用端口（洞一）+ 记忆名单任一在场判据 + 边沿唤醒 SerialPortNewlyAppeared（洞二），error_fix_log #41，build 0 错 + 123/123 通过｜状态：完成（真机四场景待验）
- STEP-21｜2026-10-30｜把 STEP-18/19/20 改动点的注释换成"人话版"（含 ConfigService 的 KnownPorts/RecordSerialPortSuccess、连接验证窗口、候选三层顺序、门铃边沿语义），仅注释无逻辑改动，build 0 错 + 123/123 通过｜状态：完成
- STEP-22｜2026-11-07｜窗口丙卡死（STEP-22 方案 A）：TryStartReconnect 理由二放宽为「SerialPortAppeared() || 有任意串口在场」，新增窗口丙单测，error_fix_log #42，build 0 错 + 124/124 通过｜状态：完成
- STEP-23｜2026-11-07｜注释规范化收尾：去除 D3 等决策编号、比喻与重复行尾注释，串口识别/构造函数/重连决策改为客观表述并保留，覆盖 DeviceService 等 8 个源码文件，build 0 错 + 124/124 通过｜状态：完成
- STEP-24｜2026-11-07｜死代码清理（方案 A）：删 IDeviceService.ShouldAlarm/GetAlarmThreshold facade、IDatabaseService.GetDeviceIdsAsync/GetMetricNamesAsync/GetDataRecordsAsync(无时间范围) 及实现，改写 DatabaseServiceTests 用时间范围版；RefreshInterval 接线到 MainViewModel 定时器（>0 兜底），build 0 错 + 124/124 通过｜状态：完成
- STEP-25｜2026-11-07｜状态语义扩展（后被 STEP-26 修正）：DataSourceState 加 Simulated，LinkHealth 曾误加 Connecting｜状态：完成
- STEP-26｜2026-11-08｜状态分层修正：DataSourceState 五态（Online/Faulted/Unavailable/Simulated/Searching），LinkHealth 七态（Handshaking=打开端口等首帧，Recovering=已确认设备）；TryConnectVerifiedAsync 验证窗口改 50ms 步进轮询，build 0 错 + 124/124 通过｜状态：完成
- STEP-27｜2026-11-08｜拔线无限 Searching 修复：ReconnectLoopAsync 无串口分支计入失败轮数，连续两轮全失败→Faulted 退出；真机复验通过（拔线约 5 秒内落 Faulted）｜状态：完成
- STEP-28｜2026-11-08｜Faulted 死锁修复（L1）：解除判据从 SerialPortNewlyAppeared()（基线每拍被刷新失效+陌生口不在记忆）改为「系统有任意串口即放行」，真机换口复验通过，build 0 错 + 124/124 通过｜状态：完成
