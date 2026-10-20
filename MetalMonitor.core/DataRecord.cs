namespace MetalMonitor.Core
{
    /// <summary>
    /// 历史数据记录实体：与数据库 DataRecords 表的一行一一对应。
    /// 由 DeviceService 三条数据通路在"三步汇合点"生成，先进批量缓冲，再单事务落库。
    /// </summary>
    public class DataRecord
    {
        /// <summary>自增主键（数据库分配，未落库前为 0）</summary>
        public int Id { get; set; }

        /// <summary>所属设备的 Guid（关联 Devices.Id）</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>所属设备名称（落库时冗余存储，便于历史查询直接展示）</summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>指标名（如 Temperature/Voltage），与 MetricConfig.Name 对应</summary>
        public string MetricName { get; set; } = string.Empty;

        /// <summary>指标值（已换算为物理量单位的 double）</summary>
        public double Value { get; set; }

        /// <summary>计量单位（如 ℃/V/mV，可能为 null 或空字符串）</summary>
        public string? Unit { get; set; }

        /// <summary>采样时间戳（本地时间 DateTime.Now）</summary>
        public DateTime Timestamp { get; set; }
    }
}
