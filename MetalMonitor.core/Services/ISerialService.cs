namespace MetalMonitor.Core
{
    /// <summary>
    /// 串口通信契约：端口枚举、连接管理与原始字节数据接收事件。
    /// </summary>
    /// <remarks>实现负责 SerialPort 的创建/配置/释放；
    /// DataReceived 事件运行在 SerialPort 后台线程，订阅方不得在回调中直接操作 UI 控件。</remarks>
    public interface ISerialService
    {
        /// <summary>枚举当前系统可用的串口名称（如 COM3、COM4）。</summary>
        string[] GetAvailablePorts();

        /// <summary>
        /// 打开指定串口（按 docs/protocol/PROTOCOL.md v0.9 配置 115200 8N1）。
        /// </summary>
        /// <param name="portName">串口名称，如 "COM3"。</param>
        Task ConnectAsync(string portName);

        /// <summary>关闭当前串口连接。</summary>
        Task DisconnectAsync();

        /// <summary>原始字节数据到达事件（后台线程触发）；载荷为原始字节，由订阅方解析。</summary>
        event EventHandler<SerialRawDataEventArgs>? DataReceived;

        /// <summary>连接状态变更事件（后台线程触发）；参数为 true=已连接 / false=已断开。</summary>
        event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>当前系统是否存在串口硬件（能力维度，每次访问实时查询）；与 <see cref="IsConnected"/>（状态维度）语义不同。</summary>
        bool IsAvailable { get; }

        /// <summary>当前是否处于已连接状态。</summary>
        bool IsConnected { get; }
    }

    /// <summary>
    /// 串口原始数据到达事件参数：携带本次读取的原始字节缓冲。
    /// </summary>
    /// <remarks>命名避开 <c>System.IO.Ports.SerialDataReceivedEventArgs</c> 冲突；
    /// 载荷可能为半帧或粘包，由上层 FrameParser 组装。</remarks>
    public sealed class SerialRawDataEventArgs : EventArgs
    {
        /// <summary>本次接收到的原始字节（未经任何帧解析）。</summary>
        public byte[] Data { get; }

        /// <summary>
        /// 构造数据事件参数。
        /// </summary>
        /// <param name="data">原始字节缓冲。</param>
        public SerialRawDataEventArgs(byte[] data)
        {
            Data = data;
        }
    }
}
