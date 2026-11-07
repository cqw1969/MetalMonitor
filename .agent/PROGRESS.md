# PROGRESS

- STEP-9｜2026-10-24｜T4.1 Core 侧：LinkHealth 五态 + MetricQuality 三态 + 两配置键 + 25 条测试（109/109）｜状态：完成
- STEP-10｜2026-10-25｜T4.1 UI 侧：顶栏数据源/链路两行状态 + 卡片质量角标与数值染色；IDeviceService 补 LinkHealth 成员（方案 B，109/109）｜状态：完成
- STEP-11｜2026-10-26｜自动重连：时钟驱动（D-13），指数退避无限重试 ±20% 抖动（D-14），Reconnecting 第六态 + 配置键 Base/Max，123/123 测试通过｜状态：完成
- STEP-12｜2026-10-28｜设备详情页异步收口（D-15 方案 A）：L77 改显式丢弃 `_ =`、L192 移到 Window Loaded await InitializeAsync，build 0 错误 + 123/123 测试通过｜状态：完成
- STEP-13｜2026-10-28｜给 InitializeAsync 首段 try-catch 兜底 GetAllMetricConfigsAsync 异常（关闭 STEP-12 引入的 async void 未捕获崩溃路径）｜状态：完成
- STEP-15｜2026-10-28｜详情页指标下拉框空白：RefreshConfigsAsync 补发 SelectedMetricName 通知（L1，error_fix_log #39）｜状态：完成（后被 STEP-17 根治取代）
- STEP-16｜2026-10-28｜运行期断线数据源恒"正常"：MaybeStartReconnect 补 SetState(Faulted)（L1，error_fix_log #40）｜状态：完成
- STEP-17｜2026-10-29｜详情页下拉框空白根治（D-16 方案 B）：SyncAvailableMetrics 增量同步替换两处 Clear+Add，保留控件选中态，123/123 + 真机三场景复验通过｜状态：完成
- STEP-18｜2026-10-29｜重连可唤醒 + 端口重现判据（D-17）：TryStartReconnect / SerialPortAppeared / WakeReconnectLoop，build 0 错 + 123/123 通过｜状态：完成（真机插拔待验）
- STEP-19｜2026-10-29｜端口漂移（D-18 D-全）：KnownPorts 候选序列 + 连接后 3 秒验证窗口，ConfigService/config.json/DeviceService 三文件，build 0 错 + 123/123 通过｜状态：完成（真机换口待验）
- STEP-20｜2026-10-30｜补洞：候选为空时遍历全部可用端口（洞一）+ 记忆名单任一在场判据 + 边沿唤醒 SerialPortNewlyAppeared（洞二），error_fix_log #41，build 0 错 + 123/123 通过｜状态：完成（真机四场景待验）
- STEP-21｜2026-10-30｜把 STEP-18/19/20 改动点的注释换成"人话版"（含 ConfigService 的 KnownPorts/RecordSerialPortSuccess、连接验证窗口、候选三层顺序、门铃边沿语义），仅注释无逻辑改动，build 0 错 + 123/123 通过｜状态：完成
- STEP-22｜2026-11-07｜窗口丙卡死（STEP-22 方案 A）：TryStartReconnect 理由二放宽为「SerialPortAppeared() || 有任意串口在场」，新增窗口丙单测，error_fix_log #42，build 0 错 + 124/124 通过｜状态：完成
