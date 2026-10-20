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
}
