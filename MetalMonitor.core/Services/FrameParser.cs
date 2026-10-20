using System;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 串口协议帧解析器：将 SerialService 推来的原始字节流组装为完整协议帧。
    /// </summary>
    /// <remarks>
    /// 设计决策
    /// - 缓冲区：128 字节环形缓冲区（byte[128] + long 计数器，无空/满歧义）
    /// - 状态机：枚举 + switch（WaitingHead→WaitingHead2→ReadingNodeId→ReadingLen→ReadingBody→ReadingCrc）
    /// - 事件载荷：解析后的 SensorDataFrame 对象（FrameParser 是"翻译官"，不是"搬运工"）
    /// - 坏帧处理：跳过帧头 1 字节，重新找 AA 55（最鲁棒，能从任意乱码中恢复）
    /// 协议参考：PROTOCOL.md v0.9 修订2，CMD 0x01 帧 12 字节（总长 = LEN + 6），CRC16/MODBUS。
    /// </remarks>
    public class FrameParser
    {
        // ── 环形缓冲区 ──
        // 用 long 计数器而非 head/tail 指针，避免"空 vs 满"的歧义
        /// <summary>环形缓冲区，容量 128 字节（可容纳 128/12 = 10 帧积压）。</summary>
        private readonly byte[] _buffer = new byte[128];
        /// <summary>累计写入字节数（永远递增，位置 = totalWritten % 128）。</summary>
        private long _totalWritten = 0;
        /// <summary>累计已解析消费字节数（永远递增，位置 = totalRead % 128）。</summary>
        private long _totalRead = 0;

        // ── 状态机 ──
        /// <summary>状态机各阶段：找帧头字节1 → 找帧头字节2 → 读NodeId → 读LEN → 读CMD+载荷 → 读CRC。</summary>
        private enum ParseState
        {
            WaitingHead,
            WaitingHead2,
            ReadingNodeId,
            ReadingLen,
            ReadingBody,
            ReadingCrc
        }
        /// <summary>当前解析状态。</summary>
        private ParseState _state = ParseState.WaitingHead;
        /// <summary>当前帧头在 _totalRead 中的起始位置（坏帧回退用）。</summary>
        private long _frameStartTotal = 0;
        /// <summary>单帧组装缓冲容量（字节）；帧总长 = LEN + 6 不得超过此值。</summary>
        private const int FrameBufferSize = 32;
        /// <summary>当前正在组装的帧的临时缓冲。</summary>
        private readonly byte[] _frameBuffer = new byte[FrameBufferSize];
        /// <summary>当前帧已写入 _frameBuffer 的字节数。</summary>
        private int _framePos = 0;
        /// <summary>当前帧预期总长（帧头2 + NODE_ID1 + LEN1 + CMD+载荷 + CRC2 = LEN + 6）。</summary>
        private int _expectedFrameLength = 0;
        /// <summary>LEN 合法下限：至少含 1 字节 CMD（LEN=0 会导致状态机在 ReadingBody 滞留越界）。</summary>
        private const int MinLen = 1;
        /// <summary>LEN 合法上限：帧总长 LEN+6 不得超过帧缓冲容量（32-6=26）。</summary>
        private const int MaxLen = FrameBufferSize - 6;

        // ── 公开属性 ──
        /// <summary>CRC 校验失败、帧头错位或 LEN 非法等坏帧累计计数。</summary>
        public int BadFrameCount { get; private set; } = 0;

        // ── 事件 ──
        /// <summary>完整帧通过 CRC 校验后触发，携带解析后的传感器数据。</summary>
        public event EventHandler<SensorDataFrame>? FrameParsed;

        // ── 公开方法 ──
        /// <summary>
        /// 喂入原始字节数据（来自 SerialService.DataReceived），追加到环形缓冲区后立即尝试解析。
        /// </summary>
        /// <param name="data">本次收到的原始字节（可能为半帧或粘包）。</param>
        public void Feed(byte[] data)
        {
            foreach (byte b in data)
            {
                _buffer[(int)(_totalWritten % _buffer.Length)] = b;
                _totalWritten++;
            }

            // 缓冲区溢出保护：未解析数据超过容量时丢弃最旧数据，重置状态机
            if (_totalWritten - _totalRead > _buffer.Length)
            {
                _totalRead = _totalWritten - _buffer.Length;//强制已消费数据和已读入的数据的差距为缓冲区长度，丢弃最旧数据
                _state = ParseState.WaitingHead;
                _framePos = 0;
            }

            TryParseFrames();
        }

        /// <summary>重置解析器状态（清空缓冲区、状态机、坏帧计数）。</summary>
        public void Reset()
        {
            _totalWritten = 0;
            _totalRead = 0;
            _state = ParseState.WaitingHead;
            _framePos = 0;
            BadFrameCount = 0;
        }

        // ── 状态机核心 ──
        /// <summary>
        /// 从环形缓冲区循环读取字节，驱动状态机。
        /// 一次调用可能触发多次 FrameParsed 事件（粘包场景）。
        /// </summary>
        private void TryParseFrames()
        {
            while (_totalWritten - _totalRead > 0)
            {
                byte b = _buffer[(int)(_totalRead % _buffer.Length)];
                _totalRead++; // 先消费字节

                switch (_state)
                {
                    case ParseState.WaitingHead:
                        if (b == 0xAA)
                        {
                            _frameStartTotal = _totalRead - 1; // 记住帧头位置
                            _frameBuffer[0] = b;
                            _framePos = 1;
                            _state = ParseState.WaitingHead2;
                        }
                        break;

                    case ParseState.WaitingHead2:
                        if (b == 0x55)
                        {
                            _frameBuffer[1] = b;
                            _framePos = 2;
                            _state = ParseState.ReadingNodeId;
                        }
                        else if (b == 0xAA)
                        {
                            // 前一个 0xAA 是误识别，当前 0xAA 可能是新帧头
                            _frameStartTotal = _totalRead - 1;
                            _frameBuffer[0] = b;
                            _framePos = 1;
                            // 保持 WaitingHead2
                        }
                        else
                        {
                            _framePos = 0;
                            _state = ParseState.WaitingHead;
                        }
                        break;

                    case ParseState.ReadingNodeId:
                        _frameBuffer[_framePos++] = b;
                        if (_framePos == 3)
                        {
                            _state = ParseState.ReadingLen;
                        }
                        break;

                    case ParseState.ReadingLen:
                        // LEN 合法性校验：噪声可能构造假帧头 AA 55，若 LEN 超出缓冲容量会导致
                        // _frameBuffer 越界（IndexOutOfRangeException）并把后续真帧当载荷吞噬；
                        // LEN=0 则会让状态机在 ReadingBody 滞留。必须在信任长度字段前拒绝。
                        if (b < MinLen || b > MaxLen)
                        {
                            BadFrameCount++;
                            _totalRead--; // 回退当前字节重新扫描：该字节可能恰好是真帧头的 0xAA
                            _framePos = 0;
                            _state = ParseState.WaitingHead;
                            break;
                        }
                        _frameBuffer[3] = b; // b = LEN
                        _framePos = 4;
                        _expectedFrameLength = b + 6; // 帧头2 + LEN1 + NodeId1 + (CMD+载荷=b) + CRC2
                        _state = ParseState.ReadingBody;
                        break;

                    case ParseState.ReadingBody:
                        _frameBuffer[_framePos++] = b;
                        // 还差 CRC 的 2 字节时转入 ReadingCrc
                        if (_framePos == _expectedFrameLength - 2)
                        {
                            _state = ParseState.ReadingCrc;
                        }
                        break;

                    case ParseState.ReadingCrc:
                        _frameBuffer[_framePos++] = b;
                        if (_framePos == _expectedFrameLength)
                        {
                            // 完整帧已读入，校验 CRC
                            ProcessFrame();
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// 校验当前帧的 CRC，通过则触发 FrameParsed 事件，失败则坏帧计数+1 并回退。
        /// </summary>
        private void ProcessFrame()
        {
            // CRC 覆盖范围：从 NodeId（偏移2）到载荷末尾，共 _expectedFrameLength - 4 字节 
            int crcDataLength = _expectedFrameLength - 4;//此处减4是因为CRC覆盖范围不包括帧头2字节和CRC2字节
            byte[] crcData = new byte[crcDataLength];
            Array.Copy(_frameBuffer, 2, crcData, 0, crcDataLength);

            ushort computedCrc = ComputeCrc16(crcData);
            // 帧内 CRC 小端：低字节在前
            int crcOffset = _expectedFrameLength - 2;
            ushort frameCrc = (ushort)(_frameBuffer[crcOffset] | (_frameBuffer[crcOffset + 1] << 8));

            if (computedCrc == frameCrc)
            {
                // CRC 通过，解析字段
                byte cmd = _frameBuffer[4];
                if (cmd == 0x01)
                {
                    // CMD 0x01：NodeId + 温度(2) + 电压(2) + 状态(1)
                    byte nodeId = _frameBuffer[2];
                    short tempRaw = (short)(_frameBuffer[5] | (_frameBuffer[6] << 8));
                    double temperature = tempRaw / 10.0;

                    ushort voltageMv = (ushort)(_frameBuffer[7] | (_frameBuffer[8] << 8));
                    byte status = _frameBuffer[9];
                    bool ds18B20Online = (status & 0x01) != 0;

                    var frame = new SensorDataFrame(nodeId, temperature, voltageMv, ds18B20Online);
                    FrameParsed?.Invoke(this, frame);
                }
                // 其他 CMD 暂不处理（未来扩展）
            }
            else
            {
                // 坏帧：跳过帧头 1 字节，回退到帧头+1 重新找 AA 55
                BadFrameCount++;
                _totalRead = _frameStartTotal + 1;
            }

            // 重置状态机，准备下一帧
            _framePos = 0;
            _state = ParseState.WaitingHead;
        }

        /// <summary>
        /// CRC-16/MODBUS 算法（复用 tools/Crc16ModbusTest 已验证实现）。
        /// 多项式 0xA001（反射形式），初值 0xFFFF，输入输出均反射。
        /// </summary>
        /// <param name="data">待校验的字节数据。</param>
        /// <returns>CRC16 值。</returns>
        private static ushort ComputeCrc16(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }
    }

    /// <summary>
    /// 传感器数据帧：FrameParser 解析后的结构化结果。
    /// </summary>
    /// <remarks>对应 PROTOCOL.md v0.9 CMD 0x01 上行数据帧的字段。</remarks>
    public class SensorDataFrame : EventArgs
    {
        ///<summary> 节点 ID：0x00=单节点/未分配（当前在用值），0x01~0xFE=多节点地址，0xFF=保留（见 PROTOCOL.md §2.1）。</summary>
        public byte NodeId { get; }
        /// <summary>温度（℃，已除10还原为浮点）。</summary>
        public double Temperature { get; }
        /// <summary>电压（mV，PA6 ADC 换算的毫伏值）。</summary>
        public ushort VoltageMv { get; }
        /// <summary>DS18B20 是否在线。</summary>
        public bool Ds18B20Online { get; }

        /// <summary>
        /// 构造传感器数据帧。
        /// </summary>
        /// <param name="nodeId">节点 ID。</param>
        /// <param name="temperature">温度（℃）。</param>
        /// <param name="voltageMv">电压（mV）。</param>
        /// <param name="ds18B20Online">DS18B20 在线状态。</param>
        public SensorDataFrame(byte nodeId,double temperature, ushort voltageMv, bool ds18B20Online)
        {
            NodeId = nodeId;
            Temperature = temperature;
            VoltageMv = voltageMv;
            Ds18B20Online = ds18B20Online;
        }
    }
}
