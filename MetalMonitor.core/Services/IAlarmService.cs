namespace MetalMonitor.Core
{
    /// <summary>
    /// 报警判断服务接口
    /// 将报警规则抽象出来，方便未来替换不同的判断逻辑
    /// </summary>
    public interface IAlarmService
    {
        /// <summary>
        /// 根据温度值判断是否应该触发报警
        /// </summary>
        /// <param name="temperature">当前温度值</param>
        /// <returns>true 表示触发报警，false 表示正常</returns>
        bool ShouldAlarm(double temperature);
        void CheckAndNotify(string name, double temperature);

        /// <summary>
        /// 获取报警阈值（用于界面显示）
        /// </summary>
        double AlarmThreshold { get; }

        /// <summary>
        /// 当报警状态发生变化时触发
        /// 参数1：设备名称，参数2：是否报警，参数3：当前温度
        /// </summary>
        event Action<string, bool, double>? AlarmStateChanged;
    }
}