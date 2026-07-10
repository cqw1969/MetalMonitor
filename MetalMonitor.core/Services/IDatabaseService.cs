using System.Collections.Generic;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    public interface IDatabaseService
    {
        Task InitializeAsync();
        Task AddTemperatureRecordAsync(string deviceId, string deviceName, double temperature);
        Task<List<TemperatureRecord>> GetTemperatureRecordsAsync(string deviceId = null);
        Task<List<TemperatureRecord>> GetTemperatureRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime);
    }
}