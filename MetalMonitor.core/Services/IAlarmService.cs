using System.Collections.Generic;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 报警判定契约：越限判断（无状态）+ 状态跳变（边沿）通知。
    /// </summary>
    /// <remarks>
    /// ShouldAlarm 只回答"当前值算不算越限"；CheckAndNotify 额外记录上一次状态，
    /// 仅在 正常→报警 / 报警→正常 的跳变瞬间触发事件，避免每秒重复通知。
    /// </remarks>
    public interface IAlarmService
    {
        /// <summary>
        /// 无状态越限判定：按指标配置的阈值与方向判断当前值是否报警。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <param name="value">当前值</param>
        /// <returns>true=越限；false=未越限或报警未启用</returns>
        bool ShouldAlarm(string metricName, double value);

        /// <summary>
        /// 边沿检测通知：与该指标上一次状态比较，仅在跳变时触发 AlarmStateChanged。
        /// </summary>
        /// <param name="deviceName">设备名（事件回传）</param>
        /// <param name="metricName">指标名</param>
        /// <param name="value">触发检测时的当前值（事件回传）</param>
        void CheckAndNotify(string deviceName, string metricName, double value);

        /// <summary>
        /// 获取指标报警阈值。
        /// </summary>
        /// <param name="metricName">指标名</param>
        /// <returns>阈值；指标不存在时返回实现约定的默认值</returns>
        double GetAlarmThreshold(string metricName);

        /// <summary>
        /// 用最新指标配置全量替换内部阈值表（配置重载时调用）。
        /// </summary>
        /// <param name="configs">指标名 -&gt; 指标配置 的字典</param>
        void UpdateThresholds(Dictionary<string, MetricConfig> configs);

        /// <summary>
        /// 报警状态跳变事件：参数依次为设备名、指标名、是否进入报警、触发值。
        /// </summary>
        event Action<string, string, bool, double>? AlarmStateChanged;
    }
}
