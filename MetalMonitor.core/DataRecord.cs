namespace MetalMonitor.Core
{
    public class DataRecord
    {
        public int Id { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string MetricName { get; set; } = string.Empty;
        public double Value { get; set; }
        public string? Unit { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
