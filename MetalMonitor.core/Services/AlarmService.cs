using System.Collections.Generic;

namespace MetalMonitor.Core
{
    public class AlarmService : IAlarmService
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, MetricConfig> _metricConfigs = new();
        private readonly Dictionary<string, bool> _previousAlarmStates = new();

        public event Action<string, string, bool, double>? AlarmStateChanged;

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

        public double GetAlarmThreshold(string metricName)
        {
            lock (_lock)
            {
                if (_metricConfigs.TryGetValue(metricName, out var config))
                    return config.AlarmThreshold;
                return 0;
            }
        }

        public void UpdateThresholds(Dictionary<string, MetricConfig> configs)
        {
            lock (_lock)
            {
                _metricConfigs.Clear();
                foreach (var kvp in configs)
                    _metricConfigs[kvp.Key] = kvp.Value;
            }
        }

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
