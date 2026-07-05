namespace MetalMonitor.Core
{
    /// <summary>
    /// 报警服务实现：温度超过阈值即报警
    /// </summary>
    public class AlarmService : IAlarmService
    {
        //AlarmThreshold 是一个属性，未来可以在界面上做一个配置框让用户修改。
        public double AlarmThreshold { get; } = 85.0;

        // 存储每台设备上一次的报警状态，用于检测变化
        private readonly Dictionary<string, bool> _previousAlarmStates = new();

        public event Action<string, bool, double>? AlarmStateChanged;

        //ShouldAlarm 方法现在很简单，但未来可以在这里加入更复杂的逻辑（比如从数据库读取动态阈值，或结合多个传感器数据）。
        public bool ShouldAlarm(double temperature)
        {
            return temperature >= AlarmThreshold;
        }

        /// <summary>
        /// 检查并触发报警状态变化事件
        /// </summary>
        public void CheckAndNotify(string deviceName, double temperature)
        {
            bool currentAlarm = ShouldAlarm(temperature);

            // 如果该设备之前没有记录，先初始化
            if (!_previousAlarmStates.ContainsKey(deviceName))
            {
                _previousAlarmStates[deviceName] = currentAlarm;
                return;
            }

            bool previousAlarm = _previousAlarmStates[deviceName];

            // 只有当状态发生变化时才触发事件
            if (currentAlarm != previousAlarm)
            {
                _previousAlarmStates[deviceName] = currentAlarm;
                AlarmStateChanged?.Invoke(deviceName, currentAlarm, temperature);
            }
        }

    }
}