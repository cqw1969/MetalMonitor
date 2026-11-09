namespace MetalMonitor.Core
{
    /// <summary>
    /// 帧路由去向：一帧的 NODE_ID 经路由判定后可能的四种结果。
    /// 边界约定见 docs/protocol/PROTOCOL.md §2.1：0x00=单节点/未分配（缺省取第一台）、
    /// 0x01~0xFE=多节点地址（查路由表）、0xFF=保留（不参与路由）。
    /// </summary>
    public enum RouteOutcome
    {
        /// <summary>按路由表命中：NODE_ID 在表中，且其指向的设备名在当前设备列表中找到。</summary>
        RoutedByTable,
        /// <summary>单节点缺省：NODE_ID=0x00 时取设备列表第一台（列表非空）。</summary>
        DefaultFirstDevice,
        /// <summary>未知节点：NODE_ID 不在路由表中（含 0xFF 保留值），应丢弃该帧并计数。</summary>
        UnknownNode,
        /// <summary>未匹配设备：路由表命中了设备名，但该名称在当前设备列表中不存在，应丢弃该帧并计数。</summary>
        UnroutedDevice
    }

    /// <summary>
    /// 帧路由器：把解析后的 NODE_ID 映射到内存设备集合中的具体设备（机制/策略分离：本类只做"映射"）。
    /// </summary>
    /// <remarks>
    /// 路由是策略：路由表由 <see cref="ConfigService.GetNodeRoutingSnapshot"/> 在启动时一次性读入，
    /// 运行中不变；本类不持有设备集合、不做 IO、无副作用，仅是纯只读判定。
    /// 命名匹配规则：0x01~0xFE 命中表后，按设备名 <c>精确匹配</c>（区分大小写）比对设备列表。
    /// </remarks>
    public sealed class FrameRouter
    {
        /// <summary>NODE_ID(0x01~0xFE) → 设备名 的启动只读路由表；构造时 null 兜底为空表。</summary>
        private readonly IReadOnlyDictionary<byte, string> _routingTable;

        /// <summary>
        /// 以启动只读路由表构造帧路由器。
        /// </summary>
        /// <param name="routingTable">NODE_ID → 设备名 路由表；为 null 时兜底为空表（任何地址都判为未知节点）。</param>
        public FrameRouter(IReadOnlyDictionary<byte, string> routingTable)
        {
            _routingTable = routingTable ?? new Dictionary<byte, string>();
        }

        /// <summary>
        /// 将一帧的 NODE_ID 解析为具体设备。
        /// </summary>
        /// <param name="nodeId">帧内的 NODE_ID 字节。</param>
        /// <param name="devices">当前内存中的设备列表（调用方传入的只读快照）。</param>
        /// <param name="device">解析结果：命中或缺省时为对应设备；未知节点/未匹配设备时为 null。</param>
        /// <returns>本次路由的去向（四选一）；调用方据结果决定是否丢弃帧及递增哪一类计数。</returns>
        public RouteOutcome Resolve(byte nodeId, IReadOnlyList<Device> devices, out Device? device)
        {
            device = null;

            // 0x00：单节点/未分配——缺省取第一台；设备列表为空则视为"未匹配设备"
            if (nodeId == 0x00)
            {
                if (devices.Count == 0)
                    return RouteOutcome.UnroutedDevice;

                device = devices[0];
                return RouteOutcome.DefaultFirstDevice;
            }

            // 0xFF：保留值——不参与路由，直接判为未知节点
            if (nodeId == 0xFF)
                return RouteOutcome.UnknownNode;

            // 0x01~0xFE：查路由表；表中无此地址 → 未知节点
            if (!_routingTable.TryGetValue(nodeId, out var deviceName))
                return RouteOutcome.UnknownNode;

            // 表中命中地址：按设备名精确匹配（区分大小写）；名称不在设备列表中 → 未匹配设备
            foreach (var candidate in devices)
            {
                if (candidate.Name == deviceName)
                {
                    device = candidate;
                    return RouteOutcome.RoutedByTable;
                }
            }
            return RouteOutcome.UnroutedDevice;
        }
    }
}
