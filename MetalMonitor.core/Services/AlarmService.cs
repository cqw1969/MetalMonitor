namespace MetalMonitor.Core
{
    /// <summary>
    /// 报警判定器：ShouldAlarm 是无状态的"是否越限"判断；
    /// CheckAndNotify 做边沿检测——只在报警状态发生跳变（无→有 / 有→无）的瞬间
    /// 发一次事件，避免心跳每秒重复触发同一报警（"边沿"借自数字电路：信号跳变的那一下）。
    /// </summary>
    public class AlarmService : IAlarmService
    {
        /// <summary>保护阈值配置表与历史报警状态表的同步锁；阈值更新、判定与边沿检测均在锁内读状态。</summary>
        private readonly object _lock = new();
        /// <summary>指标名 → 指标配置（含阈值、方向、使能开关）的判定依据表，整体替换式更新。</summary>
        private readonly Dictionary<string, MetricConfig> _metricConfigs = new();
        /// <summary>报警边沿检测的历史状态表，键为"设备名_指标名"，值为上一次的报警状态。</summary>
        private readonly Dictionary<string, bool> _previousAlarmStates = new();

        /// <summary>
        /// 报警状态跳变事件：仅在正常↔报警发生跳变的瞬间触发一次。
        /// 参数依次为设备名、指标名、当前是否报警、触发跳变时的指标值。
        /// </summary>
        /// <remarks>回调在锁外触发，避免订阅方（如 UI 线程）等待本锁而形成死锁。</remarks>
        public event Action<string, string, bool, double>? AlarmStateChanged;

        /// <summary>
        /// 无状态的阈值越限判断：依据当前阈值配置判断给定指标值是否处于报警态。
        /// </summary>
        /// <param name="metricName">指标名称（如 Temperature），须与配置表中的键一致。</param>
        /// <param name="value">待判定的指标当前值。</param>
        /// <returns>指标未配置、报警未使能时返回 false；否则按报警方向返回 value 是否触及阈值（Above 为大于等于，Below 为小于等于）。</returns>
        /// <remarks>读取配置全程持有 <see cref="_lock"/>，但不记录任何状态，可被任意线程重复调用。</remarks>
        public bool ShouldAlarm(string metricName, double value)
        {
            lock (_lock)
            {
                if (_metricConfigs.TryGetValue(metricName, out var config))
                {
                    if (!config.AlarmEnabled) return false;
                    return config.AlarmDirection == AlarmDirection.Above
                        ? value >= config.AlarmThreshold
                        : value <= config.AlarmThreshold;
                }
                return false;
            }
        }

        /// <summary>
        /// 查询指定指标当前配置的报警阈值。
        /// </summary>
        /// <param name="metricName">指标名称。</param>
        /// <returns>指标存在时返回其报警阈值；未配置时返回 0。</returns>
        public double GetAlarmThreshold(string metricName)
        {
            lock (_lock)
            {
                if (_metricConfigs.TryGetValue(metricName, out var config))
                    return config.AlarmThreshold;
                return 0;
            }
        }

        /// <summary>
        /// 整体替换报警阈值配置表（清空旧表后逐项写入）。
        /// </summary>
        /// <param name="configs">指标名 → 指标配置的最新映射，通常来自数据库 MetricConfigs 表。</param>
        /// <remarks>替换期间持锁；历史报警状态表不受影响，配置更换后沿用上一次状态继续做边沿检测。</remarks>
        public void UpdateThresholds(Dictionary<string, MetricConfig> configs)
        {
            lock (_lock)
            {
                _metricConfigs.Clear();
                foreach (var kvp in configs)
                    _metricConfigs[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// 对单个设备的单个指标做一次报警检查，并在报警状态发生跳变时触发 <see cref="AlarmStateChanged"/> 事件。
        /// </summary>
        /// <param name="deviceName">设备名称，与指标名共同构成边沿检测的唯一键。</param>
        /// <param name="metricName">指标名称。</param>
        /// <param name="value">本次采样的指标值。</param>
        /// <remarks>
        /// 边沿检测规则：首次见到的"设备_指标"只记录初始状态、不发事件；
        /// 此后仅当本次判定结果与上一次不同（正常→报警或报警→恢复）时发一次事件。
        /// 状态表的读写在 <see cref="_lock"/> 内完成，事件回调放到锁外，
        /// 避免与 UI 线程等待形成死锁。
        /// </remarks>
        public void CheckAndNotify(string deviceName, string metricName, double value)
        {
            bool currentAlarm = ShouldAlarm(metricName, value);
            string key = $"{deviceName}_{metricName}";

            bool? previousAlarm = null;
            lock (_lock)
            {
                if (!_previousAlarmStates.TryGetValue(key, out var prev))
                {
                    _previousAlarmStates[key] = currentAlarm;
                }
                else
                {
                    previousAlarm = prev;
                }
            }

            // 事件回调放到锁外，避免与 UI 线程等待形成死锁
            if (previousAlarm.HasValue && previousAlarm.Value != currentAlarm)
            {
                lock (_lock)
                {
                    _previousAlarmStates[key] = currentAlarm;
                }
                AlarmStateChanged?.Invoke(deviceName, metricName, currentAlarm, value);
            }
        }
    }
}
