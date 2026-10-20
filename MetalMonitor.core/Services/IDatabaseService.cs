using System.Collections.Generic;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 持久化契约：数据记录 / 设备 / 指标配置三组 CRUD + 数据库初始化与 WAL 检查点。
    /// </summary>
    /// <remarks>实现采用短连接（每次操作新开连接）+ 单事务批量插入；数据库为 SQLite 并启用 WAL 模式</remarks>
    public interface IDatabaseService
    {
        /// <summary>当前 SQLite 数据库文件完整路径</summary>
        string DatabasePath { get; }

        /// <summary>初始化数据库：建表、迁移旧 schema、写入默认设备与指标种子数据（幂等，可重复调用）</summary>
        Task InitializeAsync();

        /// <summary>
        /// 切换数据库文件位置并重新初始化。
        /// </summary>
        /// <param name="newPath">新目录路径（不含文件名，文件名固定 metalmonitor.db）</param>
        Task ChangeDatabasePathAsync(string newPath);

        /// <summary>执行 WAL 检查点（TRUNCATE），把 WAL 内容合并回主库并截断 WAL 文件</summary>
        Task CheckpointAsync();

        // ===== 数据记录 =====

        /// <summary>写入单条数据记录（低频/兼容路径；高频采集应走批量接口）</summary>
        /// <param name="deviceId">设备 Guid</param>
        /// <param name="deviceName">设备名（冗余存储）</param>
        /// <param name="metricName">指标名</param>
        /// <param name="value">指标值</param>
        /// <param name="unit">单位；null 表示无单位</param>
        Task AddDataRecordAsync(string deviceId, string deviceName, string metricName, double value, string? unit = null);

        /// <summary>
        /// 单事务批量插入数据记录（高频采集主路径）。
        /// </summary>
        /// <param name="records">待插入记录集合（空集合时实现应直接返回）</param>
        Task AddDataRecordsBatchAsync(List<DataRecord> records);

        /// <summary>
        /// 按设备/指标筛选全部历史记录（数据量大时优先用时间范围版本）。
        /// </summary>
        /// <param name="deviceId">可选设备 Guid 过滤；null=不限设备</param>
        /// <param name="metricName">可选指标名过滤；null=不限指标</param>
        /// <returns>按时间升序排列的记录列表</returns>
        Task<List<DataRecord>> GetDataRecordsAsync(string? deviceId = null, string? metricName = null);

        /// <summary>
        /// 按时间范围并可选设备/指标筛选历史记录。
        /// </summary>
        /// <param name="startTime">起始时间（含）</param>
        /// <param name="endTime">结束时间（含）</param>
        /// <param name="deviceId">可选设备 Guid 过滤；null=不限</param>
        /// <param name="metricName">可选指标名过滤；null=不限</param>
        /// <returns>按时间升序排列的记录列表</returns>
        Task<List<DataRecord>> GetDataRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, string? deviceId = null, string? metricName = null);

        /// <summary>获取历史记录中出现过的全部设备 Guid（历史查询下拉框用）</summary>
        /// <returns>去重后的设备 Guid 列表</returns>
        Task<List<string>> GetDeviceIdsAsync();

        /// <summary>
        /// 获取指定设备（或全部）历史记录中出现过的指标名。
        /// </summary>
        /// <param name="deviceId">可选设备 Guid；null=全部设备</param>
        /// <returns>去重后的指标名列表</returns>
        Task<List<string>> GetMetricNamesAsync(string? deviceId = null);

        // ===== 设备管理 CRUD =====

        /// <summary>获取全部设备</summary>
        /// <returns>设备实体列表；表空时由初始化种子保证至少有默认设备</returns>
        Task<List<Device>> GetAllDevicesAsync();

        /// <summary>新增设备（Id 由实体构造时预生成）</summary>
        /// <param name="device">待插入设备实体</param>
        Task AddDeviceAsync(Device device);

        /// <summary>按 Id 更新设备全部可变字段</summary>
        /// <param name="device">含新字段值的设备实体（以 Id 定位行）</param>
        Task UpdateDeviceAsync(Device device);

        /// <summary>按 Id 删除设备</summary>
        /// <param name="deviceId">设备 Guid</param>
        Task DeleteDeviceAsync(string deviceId);

        // ===== 指标配置 CRUD =====

        /// <summary>获取全部指标配置</summary>
        Task<List<MetricConfig>> GetAllMetricConfigsAsync();

        /// <summary>新增指标配置</summary>
        /// <param name="config">待插入配置实体</param>
        Task AddMetricConfigAsync(MetricConfig config);

        /// <summary>按 Id 更新指标配置</summary>
        /// <param name="config">含新字段值的配置实体</param>
        Task UpdateMetricConfigAsync(MetricConfig config);

        /// <summary>按主键 Id 删除指标配置</summary>
        /// <param name="configId">配置自增主键</param>
        Task DeleteMetricConfigAsync(int configId);
    }
}
