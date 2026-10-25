namespace MetalMonitor.Core
{
    /// <summary>
    /// 数据来源枚举：DeviceService 内部逻辑的唯一数据源标识（D1 技术债偿还，T3.4 第③步）。
    /// 边界约定：config.json 持久化值与 UI 下拉框均为显示名（中文），
    /// 由 <see cref="DataSourceExtensions"/> 在字符串 ↔ 枚举间双向映射——
    /// 显示名全项目只在映射表出现一次，新增数据源只需：加枚举成员 + 两行映射 + 一个 case。
    /// </summary>
    public enum DataSource
    {
        /// <summary>模拟数据：随机游走生成（总可用，心跳驱动）。</summary>
        Simulation,
        /// <summary>MQTT：Broker 订阅推送（可选能力，需 IMqttService 注入）。</summary>
        Mqtt,
        /// <summary>串口：FrameParser 解析帧（T3.4 预留，第⑤步接线激活）。</summary>
        Serial
    }

    /// <summary>
    /// 数据源运行状态（三态）：回答"为什么没数据"——正常 / 有硬件但连接失败 / 系统根本没有这个能力。
    /// 与 <see cref="DataSource"/>（用户选了什么）正交：用户选择永不被故障改写，状态单独表达运行结果。
    /// </summary>
    public enum DataSourceState
    {
        /// <summary>正常：所选数据源已就绪并在工作（模拟数据始终为正常）。</summary>
        Online,
        /// <summary>故障：能力存在但本次连接失败（如端口被占用、Broker 拒绝），可重试。</summary>
        Faulted,
        /// <summary>不可用：当前系统缺少该能力（如没有任何串口、服务未注册），重试也不会成功。</summary>
        Unavailable
    }

    /// <summary>数据源状态变更事件参数：携带新状态与面向用户的原因说明。</summary>
    public sealed class DataSourceStateChangedEventArgs : EventArgs
    {
        /// <summary>变更后的状态。</summary>
        public DataSourceState State { get; }
        /// <summary>状态所属数据源的显示名（"模拟数据"/"MQTT"/"串口"）。</summary>
        public string SourceDisplay { get; }
        /// <summary>失败原因（面向用户的一句话）；正常时为 null。</summary>
        public string? Reason { get; }

        /// <summary>构造状态变更事件参数。</summary>
        public DataSourceStateChangedEventArgs(DataSourceState state, string sourceDisplay, string? reason)
        {
            State = state;
            SourceDisplay = sourceDisplay;
            Reason = reason;
        }
    }

    /// <summary>
    /// 数据源显示名映射（单一事实源）："模拟数据"/"MQTT"/"串口"三种字符串全项目只在此表定义。
    /// </summary>
    public static class DataSourceExtensions
    {
        /// <summary>枚举 → 显示名（UI 下拉框与 config.json 持久化值）。</summary>
        public static string ToDisplay(this DataSource source) => source switch
        {
            DataSource.Simulation => "模拟数据",
            DataSource.Mqtt => "MQTT",
            DataSource.Serial => "串口",
            _ => "模拟数据"
        };

        /// <summary>
        /// 显示名 → 枚举（config.json 读取值与 UI 传入值的反解）。
        /// </summary>
        /// <param name="display">显示名（历史 config.json 中可能存有任意字符串）。</param>
        /// <param name="source">解析结果；未知值回退 <see cref="DataSource.Simulation"/>。</param>
        /// <returns>是否为已知显示名。</returns>
        public static bool TryToDataSource(this string display, out DataSource source)
        {
            switch (display)
            {
                case "模拟数据": source = DataSource.Simulation; return true;
                case "MQTT": source = DataSource.Mqtt; return true;
                case "串口": source = DataSource.Serial; return true;
                default: source = DataSource.Simulation; return false;
            }
        }
    }

    /// <summary>
    /// 串口链路健康（五态）：回答"这条串口通道现在还能不能送来可信数据"。
    /// 与 <see cref="DataSourceState"/>（连接结果）正交——连接成功不代表链路在送数据，
    /// 连接成功后的静默要靠时间戳判据才能发现。
    /// </summary>
    public enum LinkHealth
    {
        /// <summary>未连接：未选串口 / 已断开 / 连接失败。</summary>
        Inactive,
        /// <summary>等待首帧：已连接，但一个看门狗周期内尚未收到任何字节。</summary>
        Recovering,
        /// <summary>健康：看门狗周期内收到过通过 CRC 校验的有效帧。</summary>
        Healthy,
        /// <summary>有字节无有效帧：能收到字节，但看门狗周期内没有一帧通过校验（乱码/波特率/协议不匹配）。</summary>
        NoValidFrame,
        /// <summary>无信号：看门狗周期内连一个字节都没收到（线断/对端断电/无输出）。</summary>
        NoSignal
    }

    /// <summary>
    /// 单个指标的数据质量码：回答"这个具体数值现在还能不能信"。
    /// 写值瞬间置 <see cref="Good"/>，心跳只负责按龄期与链路状态做降级。
    /// </summary>
    public enum MetricQuality
    {
        /// <summary>良好：指标值在陈旧阈值内更新过。</summary>
        Good,
        /// <summary>陈旧：已超阈值未更新，但没有证据表明通道故障（如低频上报、对端暂停发送）。</summary>
        Stale,
        /// <summary>不可信：从未收到过，或链路故障期间的超期值。</summary>
        Bad
    }

    /// <summary>链路健康变更事件参数：携带新状态、所属数据源显示名与面向用户的原因。</summary>
    public sealed class LinkHealthChangedEventArgs : EventArgs
    {
        /// <summary>变更后的链路健康状态。</summary>
        public LinkHealth Health { get; }
        /// <summary>状态所属数据源的显示名（本阶段为"串口"）。</summary>
        public string SourceDisplay { get; }
        /// <summary>面向用户的一句话原因；健康时为 null。</summary>
        public string? Reason { get; }

        /// <summary>构造链路健康变更事件参数。</summary>
        public LinkHealthChangedEventArgs(LinkHealth health, string sourceDisplay, string? reason)
        {
            Health = health;
            SourceDisplay = sourceDisplay;
            Reason = reason;
        }
    }

    /// <summary>
    /// 链路健康纯函数评估器：无状态、无 IO，时间由调用方传入（毫秒，单调时钟），便于单测喂假时间。
    /// </summary>
    internal static class LinkHealthEvaluator
    {
        /// <summary>
        /// 按各时间戳计算链路健康状态。
        /// </summary>
        /// <param name="nowMs">当前单调毫秒时间。</param>
        /// <param name="connectedAtMs">本次连接成功的时刻；0 表示未连接。</param>
        /// <param name="lastByteMs">最近一次收到任意字节的时刻；0 表示本次连接后从未收到。</param>
        /// <param name="lastValidFrameMs">最近一次收到有效帧（CRC 通过）的时刻；0 表示从未收到。</param>
        /// <param name="watchdogSeconds">看门狗周期（秒），非正值按 1 处理。</param>
        internal static LinkHealth Evaluate(long nowMs, long connectedAtMs, long lastByteMs, long lastValidFrameMs, int watchdogSeconds)
        {
            if (connectedAtMs <= 0) return LinkHealth.Inactive;

            long watchdogMs = Math.Max(1, watchdogSeconds) * 1000L;

            // 有效帧新鲜 → 健康（有效帧必然伴随字节，优先于字节判据）
            if (lastValidFrameMs > 0 && nowMs - lastValidFrameMs <= watchdogMs)
                return LinkHealth.Healthy;

            // 有字节但有效帧超期（或从未有过有效帧）→ 能收到数据但解析不出内容
            if (lastByteMs > 0 && nowMs - lastByteMs <= watchdogMs)
                return LinkHealth.NoValidFrame;

            // 刚连上、还没见到第一个字节，且仍在等待窗口内
            if (lastByteMs == 0 && nowMs - connectedAtMs <= watchdogMs)
                return LinkHealth.Recovering;

            // 连上了，但窗口内既无字节也无有效帧（字节可能久远到这之前）
            return LinkHealth.NoSignal;
        }
    }

    /// <summary>
    /// 指标质量纯函数评估器：无状态、无 IO，时间由调用方传入（毫秒，单调时钟）。
    /// </summary>
    internal static class MetricQualityEvaluator
    {
        /// <summary>
        /// 按指标值年龄与链路故障标记计算质量码。
        /// </summary>
        /// <param name="nowMs">当前单调毫秒时间。</param>
        /// <param name="lastValueMs">最近一次写入该指标值的时刻；0 表示从未写入。</param>
        /// <param name="staleSeconds">陈旧阈值（秒），非正值按 1 处理。</param>
        /// <param name="linkFaulted">链路是否处于明确的故障态（仅串口且 Inactive/NoSignal/NoValidFrame 时为 true）。</param>
        internal static MetricQuality Evaluate(long nowMs, long lastValueMs, int staleSeconds, bool linkFaulted)
        {
            if (lastValueMs <= 0) return MetricQuality.Bad;

            long staleMs = Math.Max(1, staleSeconds) * 1000L;
            if (nowMs - lastValueMs <= staleMs) return MetricQuality.Good;

            // 超期且链路明确故障 → 不可信；链路正常或未知 → 只标陈旧（不夸大成故障）
            return linkFaulted ? MetricQuality.Bad : MetricQuality.Stale;
        }
    }
}
