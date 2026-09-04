using MetalMonitor.Core;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// AlarmService 单元测试
    ///
    /// 单元测试的基本结构：
    ///   [Fact]          — 标记一个无参数的测试方法
    ///   [Theory]        — 标记一个带参数的测试方法，配合 [InlineData] 提供多组数据
    ///   Assert.XXX      — 断言：检查实际结果是否符合预期，不符合则测试失败
    ///
    /// 命名约定：方法名_条件_预期结果
    /// </summary>
    public class AlarmServiceTests
    {
        /// <summary>
        /// 创建一个配置了温度报警的 AlarmService 实例（阈值 80℃，Above 方向）
        /// 提取为辅助方法避免每个测试都重复构造代码
        /// </summary>
        private static AlarmService CreateServiceWithTemperatureConfig(
            double threshold = 80.0,
            bool enabled = true,
            AlarmDirection direction = AlarmDirection.Above)
        {
            var service = new AlarmService();
            var config = new MetricConfig
            {
                Name = "Temperature",
                AlarmThreshold = threshold,
                AlarmEnabled = enabled,
                AlarmDirection = direction
            };
            service.UpdateThresholds(new Dictionary<string, MetricConfig>
            {
                ["Temperature"] = config
            });
            return service;
        }

        // ============================================================
        //  ShouldAlarm 测试：报警方向为 Above（超过阈值报警）
        // ============================================================

        [Theory]
        [InlineData(85.0, true)]   // 85 > 80 → 报警
        [InlineData(80.0, true)]   // 80 = 80 → 报警（>= 阈值）
        [InlineData(79.9, false)]  // 79.9 < 80 → 不报警
        [InlineData(0.0, false)]   // 极端值：0
        [InlineData(200.0, true)]  // 极端值：200
        public void ShouldAlarm_AboveDirection_VariousValues_ReturnsExpected(double value, bool expected)
        {
            // Arrange（准备）：创建服务并配置阈值
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0, direction: AlarmDirection.Above);

            // Act（执行）：调用被测方法
            bool result = service.ShouldAlarm("Temperature", value);

            // Assert（断言）：验证结果
            Assert.Equal(expected, result);
        }

        // ============================================================
        //  ShouldAlarm 测试：报警方向为 Below（低于阈值报警）
        // ============================================================

        [Theory]
        [InlineData(75.0, true)]   // 75 < 80 → 报警
        [InlineData(80.0, true)]   // 80 = 80 → 报警（<= 阈值）
        [InlineData(80.1, false)]  // 80.1 > 80 → 不报警
        [InlineData(0.0, true)]    // 极端值：0
        [InlineData(200.0, false)] // 极端值：200
        public void ShouldAlarm_BelowDirection_VariousValues_ReturnsExpected(double value, bool expected)
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0, direction: AlarmDirection.Below);

            bool result = service.ShouldAlarm("Temperature", value);

            Assert.Equal(expected, result);
        }

        // ============================================================
        //  ShouldAlarm 测试：报警被禁用时不报警
        // ============================================================

        [Fact]
        public void ShouldAlarm_AlarmDisabled_AlwaysReturnsFalse()
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0, enabled: false);

            // 即使值远超阈值，禁用后也不应报警
            bool result = service.ShouldAlarm("Temperature", 999.0);

            Assert.False(result);
        }

        // ============================================================
        //  ShouldAlarm 测试：未配置的指标不报警
        // ============================================================

        [Fact]
        public void ShouldAlarm_UnknownMetric_ReturnsFalse()
        {
            var service = CreateServiceWithTemperatureConfig();

            // "Pressure" 没有配置过，任何值都不应报警
            bool result = service.ShouldAlarm("Pressure", 999.0);

            Assert.False(result);
        }

        // ============================================================
        //  GetAlarmThreshold 测试
        // ============================================================

        [Fact]
        public void GetAlarmThreshold_ConfiguredMetric_ReturnsThreshold()
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 75.5);

            double threshold = service.GetAlarmThreshold("Temperature");

            Assert.Equal(75.5, threshold);
        }

        [Fact]
        public void GetAlarmThreshold_UnknownMetric_ReturnsZero()
        {
            var service = CreateServiceWithTemperatureConfig();

            double threshold = service.GetAlarmThreshold("Pressure");

            Assert.Equal(0.0, threshold);
        }

        // ============================================================
        //  UpdateThresholds 测试：更新后旧配置被替换
        // ============================================================

        [Fact]
        public void UpdateThresholds_NewConfig_ReplacesOldConfig()
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0);

            // 用新阈值替换
            service.UpdateThresholds(new Dictionary<string, MetricConfig>
            {
                ["Temperature"] = new MetricConfig
                {
                    Name = "Temperature",
                    AlarmThreshold = 50.0,
                    AlarmEnabled = true,
                    AlarmDirection = AlarmDirection.Above
                }
            });

            // 阈值应已变为 50
            Assert.Equal(50.0, service.GetAlarmThreshold("Temperature"));
            // 60 >= 50 → 报警（用旧阈值 80 不会报警）
            Assert.True(service.ShouldAlarm("Temperature", 60.0));
        }

        [Fact]
        public void UpdateThresholds_EmptyDict_ClearsAllConfigs()
        {
            var service = CreateServiceWithTemperatureConfig();

            // 传入空字典，清除所有配置
            service.UpdateThresholds(new Dictionary<string, MetricConfig>());

            Assert.Equal(0.0, service.GetAlarmThreshold("Temperature"));
            Assert.False(service.ShouldAlarm("Temperature", 999.0));
        }

        // ============================================================
        //  CheckAndNotify 测试：报警状态变化时触发事件
        // ============================================================

        [Fact]
        public void CheckAndNotify_StateNormalToAlarm_FiresEvent()
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0);
            var firedEvents = new List<(string device, string metric, bool isAlarming, double value)>();
            service.AlarmStateChanged += (device, metric, isAlarming, value) =>
                firedEvents.Add((device, metric, isAlarming, value));

            // 第一次调用：正常 → 报警（85 >= 80），这是首次记录状态，不触发事件
            service.CheckAndNotify("Device1", "Temperature", 85.0);
            Assert.Empty(firedEvents); // 首次记录不触发

            // 第二次调用：报警 → 正常（70 < 80），状态变化，触发事件
            service.CheckAndNotify("Device1", "Temperature", 70.0);
            Assert.Single(firedEvents);
            Assert.Equal("Device1", firedEvents[0].device);
            Assert.Equal("Temperature", firedEvents[0].metric);
            Assert.False(firedEvents[0].isAlarming); // 变为正常
            Assert.Equal(70.0, firedEvents[0].value);
        }

        [Fact]
        public void CheckAndNotify_StateUnchanged_DoesNotFireEvent()
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0);
            var firedEvents = new List<(string, string, bool, double)>();
            service.AlarmStateChanged += (device, metric, isAlarming, value) =>
                firedEvents.Add((device, metric, isAlarming, value));

            // 首次：正常状态（70），记录初始状态
            service.CheckAndNotify("Device1", "Temperature", 70.0);
            Assert.Empty(firedEvents);

            // 再次：仍然正常（75），状态未变，不触发
            service.CheckAndNotify("Device1", "Temperature", 75.0);
            Assert.Empty(firedEvents);
        }

        [Fact]
        public void CheckAndNotify_DifferentDevices_IndependentStates()
        {
            var service = CreateServiceWithTemperatureConfig(threshold: 80.0);
            var firedEvents = new List<(string device, string metric, bool isAlarming, double value)>();
            service.AlarmStateChanged += (device, metric, isAlarming, value) =>
                firedEvents.Add((device, metric, isAlarming, value));

            // Device1 首次记录（85 报警）
            service.CheckAndNotify("Device1", "Temperature", 85.0);
            // Device2 首次记录（70 正常）
            service.CheckAndNotify("Device2", "Temperature", 70.0);
            Assert.Empty(firedEvents); // 都是首次记录，不触发

            // Device1 从报警变正常（70）
            service.CheckAndNotify("Device1", "Temperature", 70.0);
            Assert.Single(firedEvents);
            Assert.Equal("Device1", firedEvents[0].device);

            // Device2 从正常变报警（90）
            service.CheckAndNotify("Device2", "Temperature", 90.0);
            Assert.Equal(2, firedEvents.Count);
            Assert.Equal("Device2", firedEvents[1].device);
            Assert.True(firedEvents[1].isAlarming);
        }
    }
}
