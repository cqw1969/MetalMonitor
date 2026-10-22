using System.IO.Ports;
using System.Threading.Tasks;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 串口数据源。按 PROTOCOL.md v0.9 配置：115200 波特率、8 数据位、1 停止位、无校验。
    /// 注意：DataReceived/ConnectionStateChanged 事件由 SerialPort 内部后台线程触发（非 UI 线程）。
    /// 本服务仅负责收原始字节，不做帧解析（帧组装与 CRC 校验由 T3.3 FrameParser 负责）。
    /// </summary>
    public class SerialService : ISerialService, IDisposable
    {
        /// <summary>底层串口实例，承载端口配置与数据收发。</summary>
        private SerialPort? _serialPort;
        /// <summary>对象释放标记，防止 Dispose 重复释放底层串口。</summary>
        private bool _disposed = false;

        /// <summary>获取当前串口是否处于打开状态。</summary>
        public bool IsConnected => _serialPort?.IsOpen ?? false;

        // 能力维度：每次访问实时查询注册表端口名单（本机实测约 26 微秒/次），
        // USB 串口热插拔后无需重启进程即可反映真实状态（D-3 方案 A：动态计算）
        /// <summary>当前系统是否存在串口硬件（动态查询，热插拔即时反映）。</summary>
        public bool IsAvailable => SerialPort.GetPortNames().Length > 0;

        /// <summary>
        /// 原始字节数据到达事件；由 SerialPort 内部后台线程触发（非 UI 线程），
        /// 事件参数携带本次读取的原始字节（可能为半帧或粘包，未经任何解析）。
        /// </summary>
        public event EventHandler<SerialRawDataEventArgs>? DataReceived;
        /// <summary>连接状态变更事件（连接成功/断开时触发），在后台线程上回调。</summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// 创建串口服务实例；构造时不打开任何端口，等待 ConnectAsync 调用。
        /// </summary>
        public SerialService()
        {
        }

        /// <summary>枚举当前系统可用的串口名称（如 COM3、COM4）。</summary>
        /// <returns>可用串口名称数组；无可用端口时返回空数组。</returns>
        public string[] GetAvailablePorts()
        {
            return SerialPort.GetPortNames();
        }

        /// <summary>
        /// 打开指定串口，按 PROTOCOL.md v0.9 §1 配置 115200 8N1。
        /// </summary>
        /// <param name="portName">串口名称，如 "COM3"。</param>
        /// <returns>表示异步连接操作的任务。</returns>
        /// <remarks>
        /// 波特率 115200、数据位 8、停止位 1、校验无、流控无；
        /// 已打开时先关闭再重新打开；连接成功后触发 <see cref="ConnectionStateChanged"/> 事件。
        /// </remarks>
        public async Task ConnectAsync(string portName)
        {
            // 能力预检：目标端口不在当前系统端口名单中 → 抛 InvalidOperationException（能力缺失），
            // 与 Open() 可能抛出的 IOException（端口被占用等状态故障）区分开，供上层分别处理
            var existingPorts = SerialPort.GetPortNames();
            if (Array.FindIndex(existingPorts, p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                throw new InvalidOperationException(
                    $"串口 {portName} 不存在于当前系统（可用端口: {(existingPorts.Length > 0 ? string.Join(", ", existingPorts) : "无")}）。");
            }

            // 已有端口打开时先清理
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.DataReceived -= OnSerialDataReceived;
                _serialPort.Close();
            }

            // 创建并配置串口（参数来自 PROTOCOL.md v0.9 §1 物理层）
            _serialPort = new SerialPort(portName, 115200)
            {
                DataBits = 8,
                StopBits = StopBits.One,
                Parity = Parity.None,
                Handshake = Handshake.None,
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };

            _serialPort.DataReceived += OnSerialDataReceived;

            // SerialPort.Open() 是同步操作，包装为异步避免阻塞 UI 线程
            await Task.Run(() =>
            {
                _serialPort.Open();
            }).ConfigureAwait(false);

            Console.WriteLine($"[Serial] 已连接 {portName} (115200 8N1)");
            ConnectionStateChanged?.Invoke(this, true);
        }

        /// <summary>
        /// 关闭当前串口连接；未打开时直接返回，不做任何操作。
        /// </summary>
        /// <returns>表示异步断开操作的任务。</returns>
        public async Task DisconnectAsync()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                await Task.Run(() =>
                {
                    _serialPort.DataReceived -= OnSerialDataReceived;
                    _serialPort.Close();
                }).ConfigureAwait(false);

                Console.WriteLine("[Serial] 连接已断开");
                ConnectionStateChanged?.Invoke(this, false);
            }
        }

        // SerialPort 内部后台线程回调：读取可用字节并原样广播，不做帧解析（职责单一）
        /// <summary>
        /// SerialPort 数据接收回调：读取所有可用字节后原样广播给订阅者。
        /// </summary>
        /// <param name="sender">事件发送者（SerialPort 实例）。</param>
        /// <param name="e">SerialPort 数据事件参数。</param>
        /// <remarks>运行在 SerialPort 内部后台线程；本方法不做任何帧解析，组装与校验由 T3.3 FrameParser 负责。</remarks>
        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                int bytesToRead = _serialPort.BytesToRead;
                if (bytesToRead <= 0)
                    return;

                byte[] buffer = new byte[bytesToRead];
                _serialPort.Read(buffer, 0, bytesToRead);

                Console.WriteLine($"[Serial] 收到 {bytesToRead} 字节: {BitConverter.ToString(buffer)}");
                DataReceived?.Invoke(this, new SerialRawDataEventArgs(buffer));
            }
            catch (Exception ex)
            {
                // 后台线程异常不能冒泡到全局处理器，静默记录后吞掉
                Console.WriteLine($"[Serial] 数据接收异常: {ex.Message}");
            }
        }

        /// <summary>释放底层串口等托管资源，并请求 GC 不再调用终结器。</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源的统一实现（标准 Dispose 模式），重复调用安全。
        /// </summary>
        /// <param name="disposing">true 表示由 Dispose 调用，可释放托管资源；false 表示由终结器调用，仅清理非托管资源。</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                if (_serialPort != null)
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.DataReceived -= OnSerialDataReceived;
                        _serialPort.Close();
                    }
                    _serialPort.Dispose();
                }
            }

            _disposed = true;
        }

        /// <summary>终结器：未显式 Dispose 时兜底释放，按非托管释放路径调用 <see cref="Dispose(bool)"/>。</summary>
        ~SerialService()
        {
            Dispose(false);
        }
    }
}
