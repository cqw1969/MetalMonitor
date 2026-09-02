using System.Collections.Generic;

namespace MetalMonitor.Core
{
    public interface IAlarmService
    {
        bool ShouldAlarm(string metricName, double value);
        void CheckAndNotify(string deviceName, string metricName, double value);
        double GetAlarmThreshold(string metricName);
        void UpdateThresholds(Dictionary<string, MetricConfig> configs);
        event Action<string, string, bool, double>? AlarmStateChanged;
    }
}
