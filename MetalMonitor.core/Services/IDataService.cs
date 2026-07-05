namespace MetalMonitor.Core
{
    public interface IDataService
    {
        /// <summary>
        /// 获取所有设备的最新数据
        /// </summary>
        List<Device> GetAllDevices();
    }
}