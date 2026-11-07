using MetalMonitor.Core;
using System.Collections.Generic;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// FrameParser 状态机单元测试。
    ///
    /// 测试用例（正常帧/半帧/粘包/坏 CRC/NODE_ID）：
    ///   1. 正常帧：完整 12 字节帧一次性喂入 → 触发 FrameParsed，NODE_ID/温度/电压/状态正确
    ///   2. 半帧：分两次喂入（先 5 字节后 7 字节）→ 正确解析
    ///   3. 粘包：一次喂入 24 字节（两帧连发）→ 触发两次 FrameParsed
    ///   4. 坏 CRC：篡改帧末 CRC 字节 → 不触发事件，BadFrameCount = 1
    ///   5. 帧头错位：在垃圾字节后插入正常帧 → 跳过垃圾，解析正常帧
    ///   6. 坏 CRC 后接正常帧 → 恢复并只解析正常帧
    ///   7. LEN 超上限（0xFF > 26）：拒绝假帧头后仍能解析后续真帧
    ///   8. LEN=0（低于下限 1）被拒绝，不触发事件
    ///   9. LEN 字节恰为 0xAA（真帧头第一字节）：回退后真帧正常解析
    ///  10. 超大 LEN 后跟大量垃圾字节，不得抛 IndexOutOfRangeException
    ///  11. NODE_ID 提取：NODE_ID=0x2A 的帧 → NodeId 属性正确
    ///      （解析器只提取不判权责——路由判定属上位机策略层，见 PROTOCOL.md §2.1）
    ///
    /// 标准帧（来自 PROTOCOL.md v0.9 修订2 §7 示例）：
    ///   AA 55 00 06 01 FD 00 E2 0C 01 2E F8
    ///   NODE_ID=0x00（单节点/未分配），温度 25.3℃, 电压 3298mV, DS18B20 在线
    ///   CRC 覆盖偏移 2~9 共 8 字节（NODE_ID+LEN+CMD+载荷），0xF82E
    /// </summary>
    public class FrameParserTests
    {
        /// <summary>PROTOCOL.md v0.9 修订2 §7 标准示例帧（12 字节）。</summary>
        private static readonly byte[] _validFrame =
        {
            0xAA, 0x55, 0x00, 0x06, 0x01, 0xFD, 0x00, 0xE2, 0x0C, 0x01, 0x2E, 0xF8
        };

        /// <summary>NODE_ID=0x2A 的帧：CRC 覆盖 2A 06 01 FD 00 E2 0C 01 → 0x9FAC（帧内小端 AC 9F）。</summary>
        private static readonly byte[] _frameNodeId2A =
        {
            0xAA, 0x55, 0x2A, 0x06, 0x01, 0xFD, 0x00, 0xE2, 0x0C, 0x01, 0xAC, 0x9F
        };

        // ═══════════════════════════════════════════════════════════
        // 用例 1：正常帧一次性喂入
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_CompleteFrame_ParsesCorrectly()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            parser.Feed(_validFrame);

            Assert.Single(parsed);
            Assert.Equal(25.3, parsed[0].Temperature, 1);
            Assert.Equal((ushort)3298, parsed[0].VoltageMv);
            Assert.True(parsed[0].Ds18B20Online);
            Assert.Equal(0x00, parsed[0].NodeId);
        }

        // ═══════════════════════════════════════════════════════════
        // 用例 2：半帧分两次喂入
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_SplitFrame_ParsesAcrossTwoFeeds()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 先喂前 5 字节（帧头2 + NODE_ID + LEN + CMD，不足以成帧）
            parser.Feed(new byte[] { 0xAA, 0x55, 0x00, 0x06, 0x01 });
            Assert.Empty(parsed); // 半帧不应触发事件

            // 再喂后 7 字节（载荷 5 + CRC 2），补齐全帧
            parser.Feed(new byte[] { 0xFD, 0x00, 0xE2, 0x0C, 0x01, 0x2E, 0xF8 });
            Assert.Single(parsed);
            Assert.Equal(25.3, parsed[0].Temperature, 1);
        }

        // ═══════════════════════════════════════════════════════════
        // 用例 3：粘包——两帧一次性喂入
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_TwoConcatenatedFrames_ParsesBoth()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 两帧粘在一起（24 字节）
            var twoFrames = new byte[24];
            _validFrame.CopyTo(twoFrames, 0);
            _validFrame.CopyTo(twoFrames, 12);

            parser.Feed(twoFrames);

            Assert.Equal(2, parsed.Count);
            Assert.Equal(25.3, parsed[0].Temperature, 1);
            Assert.Equal(25.3, parsed[1].Temperature, 1);
        }

        // ═══════════════════════════════════════════════════════════
        // 用例 4：坏 CRC——篡改帧末两字节（偏移 10-11）
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_BadCrc_NoEventAndBadFrameCountIncremented()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 篡改 CRC 字节为 0xFF 0xFF
            var badFrame = (byte[])_validFrame.Clone();
            badFrame[10] = 0xFF;
            badFrame[11] = 0xFF;

            parser.Feed(badFrame);

            Assert.Empty(parsed); // 坏帧不应触发事件
            Assert.Equal(1, parser.BadFrameCount);
        }

        // ═══════════════════════════════════════════════════════════
        // 用例 5：帧头错位——垃圾字节后跟正常帧
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_GarbageBeforeFrame_SkipsGarbageParsesFrame()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 前面 3 字节垃圾，后面跟正常帧
            var garbageAndFrame = new byte[3 + 12];
            garbageAndFrame[0] = 0x00;
            garbageAndFrame[1] = 0x11;
            garbageAndFrame[2] = 0x22;
            _validFrame.CopyTo(garbageAndFrame, 3);

            parser.Feed(garbageAndFrame);

            Assert.Single(parsed);
            Assert.Equal(25.3, parsed[0].Temperature, 1);
        }

        // ═══════════════════════════════════════════════════════════
        // 额外用例：连续坏帧后恢复——验证坏帧恢复能力
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_BadCrcThenValidFrame_RecoversAndParsesValidFrame()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 先喂坏帧
            var badFrame = (byte[])_validFrame.Clone();
            badFrame[10] = 0xFF;
            badFrame[11] = 0xFF;
            parser.Feed(badFrame);

            // 再喂正常帧
            parser.Feed(_validFrame);

            Assert.Single(parsed); // 只有正常帧被解析
            Assert.Equal(1, parser.BadFrameCount);
            Assert.Equal(25.3, parsed[0].Temperature, 1);
        }

        // ═══════════════════════════════════════════════════════════
        // LEN 校验用例 1：LEN 超过上限（0xFF=255 > 26），拒绝假帧头后仍能解析后续真帧
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_InvalidLenTooLarge_RejectsAndRecoversToFollowingFrame()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 假帧头 AA 55 + NODE_ID=0x00 + 非法 LEN=0xFF，紧跟一个真帧
            var data = new byte[4 + 12];
            data[0] = 0xAA;
            data[1] = 0x55;
            data[2] = 0x00;
            data[3] = 0xFF;
            _validFrame.CopyTo(data, 4);

            parser.Feed(data);

            Assert.Single(parsed); // 假帧头被拒绝，真帧正常解析
            Assert.Equal(25.3, parsed[0].Temperature, 1);
            Assert.Equal(1, parser.BadFrameCount);
        }

        // ═══════════════════════════════════════════════════════════
        // LEN 校验用例 2：LEN=0（低于下限 1）被拒绝，不触发事件
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_InvalidLenZero_RejectedNoEvent()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // AA 55 + NODE_ID=0x00 + LEN=0
            parser.Feed(new byte[] { 0xAA, 0x55, 0x00, 0x00 });

            Assert.Empty(parsed);
            Assert.Equal(1, parser.BadFrameCount);
        }

        // ═══════════════════════════════════════════════════════════
        // LEN 校验用例 3：LEN 字节恰为 0xAA（真帧头第一字节）
        // 新协议下假帧头先吃掉 NODE_ID 字节；LEN 位置的 0xAA 被拒绝后回退，
        // 该 AA 重新参与帧头扫描，紧随的真帧头由 WaitingHead2 的"连续 AA"分支接住
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_InvalidLenByteIsRealHeadAA_RewindsAndParsesRealFrame()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 假帧头 AA 55 + NODE_ID=0x00，LEN 位置恰为真帧的帧头 AA
            var data = new byte[4 + 12];
            data[0] = 0xAA;
            data[1] = 0x55;
            data[2] = 0x00;
            data[3] = 0xAA;
            _validFrame.CopyTo(data, 4);

            parser.Feed(data);

            Assert.Single(parsed); // 回退后该 AA 被重新识别，真帧正常解析
            Assert.Equal(25.3, parsed[0].Temperature, 1);
            Assert.Equal(1, parser.BadFrameCount);
        }

        // ═══════════════════════════════════════════════════════════
        // LEN 校验用例 4（越界回归）：超大 LEN 后跟大量垃圾字节，不得抛 IndexOutOfRangeException
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_OversizedLenFollowedByGarbage_DoesNotThrow()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            // 假帧头 AA 55 + NODE_ID=0x00 + LEN=0xFF + 40 字节垃圾（全部避开 0xAA，确定性测试）
            var data = new byte[4 + 40];
            data[0] = 0xAA;
            data[1] = 0x55;
            data[2] = 0x00;
            data[3] = 0xFF;
            for (int i = 4; i < data.Length; i++)
            {
                data[i] = 0x11;
            }

            var exception = Record.Exception(() => parser.Feed(data));

            Assert.Null(exception); // 修复前此处会抛 IndexOutOfRangeException
            Assert.Empty(parsed);
            Assert.Equal(1, parser.BadFrameCount);
        }

        // ═══════════════════════════════════════════════════════════
        // NODE_ID 提取用例：NODE_ID=0x2A 的帧被完整解析，NodeId 属性正确
        // 解析器不校验"0x2A 是否在路由表"，该职责属于上位机路由层
        // ═══════════════════════════════════════════════════════════
        [Fact]
        public void Feed_FrameWithNodeId2A_ExtractsNodeIdCorrectly()
        {
            var parser = new FrameParser();
            var parsed = new List<SensorDataFrame>();
            parser.FrameParsed += (s, e) => parsed.Add(e);

            parser.Feed(_frameNodeId2A);

            Assert.Single(parsed);
            Assert.Equal(0x2A, parsed[0].NodeId);
            Assert.Equal(25.3, parsed[0].Temperature, 1);
            Assert.Equal((ushort)3298, parsed[0].VoltageMv);
            Assert.Equal(0, parser.BadFrameCount);
        }
    }
}
