namespace MetalMonitor.Core
{
    /// <summary>
    /// 设备业务门面契约：UI 层访问设备/指标/数据源的统一入口。
    /// 依赖接口而非具体类，UI 不必知道 DeviceService 内部结构，测试也可注入替身。
    /// </summary>
    public interface IDeviceService
    {
        /// <summary>历史曲线保留的最大数据点数（超出滚动丢弃旧点）</summary>
        int MaxHistoryPoints { get; }

        /// <summary>当前数据源显示名（模拟数据/MQTT/串口）</summary>
        string DataSource { get; }

        /// <summary>可供 UI 下拉选择的全部数据源名称</summary>
        string[] AvailableDataSources { get; }

        /// <summary>当前数据源的运行状态（正常/故障/不可用）；用户所选数据源本身不因故障改变</summary>
        DataSourceState State { get; }

        /// <summary>最近一次数据源故障的原因说明；状态正常时为 null</summary>
        string? LastDataSourceError { get; }

        /// <summary>数据源状态变更通知：切换完成即触发（成功与失败都会），UI 据此提示或调整选项可用性</summary>
        event EventHandler<DataSourceStateChangedEventArgs>? DataSourceStateChanged;

        /// <summary>当前串口链路健康状态（未选串口/未连接时为 Inactive）；只在跃迁时广播</summary>
        LinkHealth LinkHealth { get; }

        /// <summary>链路健康变更通知：仅在状态真正跃迁时触发（每秒心跳不重复广播同值）</summary>
        event EventHandler<LinkHealthChangedEventArgs>? LinkHealthChanged;

        /// <summary>获取全部设备的快照列表（线程安全拷贝）</summary>
        /// <returns>设备列表副本；调用方可自由遍历，不受后续集合增删影响</returns>
        List<Device> GetDevices();

        /// <summary>
        /// 心跳推进：同步设备/配置（仅首次）、按当前数据源产生数据、达阈值批量落库、周期回收 WAL。
        /// </summary>
        /// <remarks>由 UI 定时器按配置间隔调用；MQTT/串口数据由事件驱动，不经此方法</remarks>
        Task UpdateDevicesAsync();

        /// <summary>
        /// 切换数据源；切换前强制冲刷缓冲，连接失败时回退模拟数据。
        /// </summary>
        /// <param name="source">数据源名（须为 <see cref="AvailableDataSources"/> 之一）</param>
        Task SetDataSourceAsync(string source);

        /// <summary>强制重新从数据库同步设备列表（增删改设备后调用）</summary>
        Task ReloadDevicesAsync();

        /// <summary>重新加载指标配置并同步报警阈值（修改指标配置后调用）</summary>
        Task ReloadMetricConfigsAsync();

        /// <summary>获取当前生效的指标配置集合</summary>
        /// <returns>指标配置列表；未加载时为空列表</returns>
        List<MetricConfig> GetMetricConfigs();
    }
}
