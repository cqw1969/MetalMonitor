using MetalMonitor.Core;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// 数据源显示名映射单测（T3.4 第③步，D1 抽枚举）。
    /// 显示名全项目只在 DataSourceExtensions 定义一次；本类锁定三个显示名的精确值与双向映射——
    /// 改显示名会先在这里红，防止 UI 下拉框与 config.json 持久化值静默失配。
    /// </summary>
    public class DataSourceTests
    {
        [Theory]
        [InlineData(DataSource.Simulation, "模拟数据")]
        [InlineData(DataSource.Mqtt, "MQTT")]
        [InlineData(DataSource.Serial, "串口")]
        public void ToDisplay_ReturnsExactConfiguredNames(DataSource source, string expected)
        {
            Assert.Equal(expected, source.ToDisplay());
        }

        [Theory]
        [InlineData(DataSource.Simulation, "模拟数据")]
        [InlineData(DataSource.Mqtt, "MQTT")]
        [InlineData(DataSource.Serial, "串口")]
        public void TryToDataSource_RoundTrip_ReturnsSameMember(DataSource source, string display)
        {
            Assert.True(display.TryToDataSource(out var parsed));
            Assert.Equal(source, parsed);
        }

        [Theory]
        [InlineData("mqtt")]   // 大小写敏感：配置值必须与映射表逐字一致
        [InlineData("Ble")]    // 未知数据源
        [InlineData("")]       // 空串（ConfigService 读取时会兜底，此处防裸调用）
        public void TryToDataSource_UnknownName_FallsBackToSimulation(string display)
        {
            Assert.False(display.TryToDataSource(out var parsed));
            Assert.Equal(DataSource.Simulation, parsed);
        }
    }
}
