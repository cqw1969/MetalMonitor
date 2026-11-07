using MetalMonitor.Core;
using System.Collections.Generic;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// 帧路由器单测：锁定 NODE_ID 到设备的四类去向判定。
    /// 路由是"策略"（config.json 路由表 + 内存设备列表），本类锁定其边界语义——
    /// 0x00 缺省取第一台、表命中按名精确匹配、未知节点/未匹配设备均丢弃，
    /// 防止接入层把一帧静默误路由到错误设备。
    /// </summary>
    public class FrameRouterTests
    {
        private static List<Device> Devices(params Device[] devices) => new(devices);

        [Fact]
        public void Resolve_NodeIdZero_ReturnsFirstDevice()
        {
            var first = new Device("1号冲压机");
            var router = new FrameRouter(new Dictionary<byte, string>());

            var outcome = router.Resolve(0x00, Devices(first, new Device("2号CNC")), out var device);

            Assert.Equal(RouteOutcome.DefaultFirstDevice, outcome);
            Assert.Same(first, device);
        }

        [Fact]
        public void Resolve_NodeIdZero_EmptyDeviceList_ReturnsUnroutedDevice()
        {
            var router = new FrameRouter(new Dictionary<byte, string>());

            var outcome = router.Resolve(0x00, Devices(), out var device);

            Assert.Equal(RouteOutcome.UnroutedDevice, outcome);
            Assert.Null(device);
        }

        [Fact]
        public void Resolve_NodeIdInRoutingTable_ReturnsMappedDevice()
        {
            var target = new Device("3号加热炉");
            var router = new FrameRouter(new Dictionary<byte, string> { [0x2A] = "3号加热炉" });

            var outcome = router.Resolve(0x2A, Devices(new Device("1号冲压机"), target), out var device);

            Assert.Equal(RouteOutcome.RoutedByTable, outcome);
            Assert.Same(target, device);
        }

        [Fact]
        public void Resolve_NodeIdNotInRoutingTable_ReturnsUnknownNode()
        {
            var router = new FrameRouter(new Dictionary<byte, string> { [0x2A] = "3号加热炉" });

            var outcome = router.Resolve(0x0B, Devices(new Device("3号加热炉")), out var device);

            Assert.Equal(RouteOutcome.UnknownNode, outcome);
            Assert.Null(device);
        }

        [Fact]
        public void Resolve_MappedDeviceNameMissingFromDevices_ReturnsUnroutedDevice()
        {
            var router = new FrameRouter(new Dictionary<byte, string> { [0x2A] = "幽灵设备" });

            var outcome = router.Resolve(0x2A, Devices(new Device("1号冲压机")), out var device);

            Assert.Equal(RouteOutcome.UnroutedDevice, outcome);
            Assert.Null(device);
        }

        [Fact]
        public void Resolve_ReservedNodeId_ReturnsUnknownNode()
        {
            // 0xFF 为保留值：即便路由表里存在该键也不参与路由
            var router = new FrameRouter(new Dictionary<byte, string> { [0xFF] = "1号冲压机" });

            var outcome = router.Resolve(0xFF, Devices(new Device("1号冲压机")), out var device);

            Assert.Equal(RouteOutcome.UnknownNode, outcome);
            Assert.Null(device);
        }

        [Fact]
        public void Resolve_NullRoutingTable_FallsBackToEmptyTable()
        {
            var router = new FrameRouter(null!);

            var outcome = router.Resolve(0x2A, Devices(new Device("1号冲压机")), out var device);

            Assert.Equal(RouteOutcome.UnknownNode, outcome);
            Assert.Null(device);
        }
    }
}
