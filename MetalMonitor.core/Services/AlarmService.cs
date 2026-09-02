using System.Collections.Generic;

namespace MetalMonitor.Core
{
    public class AlarmService : IAlarmService
    {
        private readonly Dictionary<string, MetricConfig> _metricConfigs = new();
        private readonly Dictionary<string, bool> _previousAlarmStates = new();

        public event Action<string, string, bool, double>? AlarmStateChanged;

        public bool ShouldAlarm(string metricName, double value)
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

        public double GetAlarmThreshold(string metricName)
        {
            if (_metricConfigs.TryGetValue(metricName, out var config))
                return config.AlarmThreshold;
            return 0;
        }

        public void UpdateThresholds(Dictionary<string, MetricConfig> configs)
        {
            _metricConfigs.Clear();
            foreach (var kvp in configs)
                _metricConfigs[kvp.Key] = kvp.Value;
        }

        public void CheckAndNotify(string deviceName, string metricName, double value)
        {
            bool currentAlarm = ShouldAlarm(metricName, value);
            string key = $"{deviceName}_{metricName}";

            if (!_previousAlarmStates.ContainsKey(key))
            {
                _previousAlarmStates[key] = currentAlarm;
                return;
            }

            bool previousAlarm = _previousAlarmStates[key];

            if (currentAlarm != previousAlarm)
            {
                _previousAlarmStates[key] = currentAlarm;
                AlarmStateChanged?.Invoke(deviceName, metricName, currentAlarm, value);
            }
        }
    }
}
