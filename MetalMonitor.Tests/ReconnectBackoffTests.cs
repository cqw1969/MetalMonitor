using MetalMonitor.Core;
using System;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// STEP-11 / D-13：指数退避 + 抖动纯函数测试。
    /// </summary>
    /// <remarks>
    /// 所有测试用单一 Random 种子（0），保证抖动结果可复现、不引入随机性脆弱性。
    /// </remarks>
    public class ReconnectBackoffTests
    {
        private static Random NewSeeded => new Random(0);

        [Theory]
        // attempt=0 → base * 2^0 = base（首次立即试，无翻倍）
        [InlineData(0, 1, 30, 1000)]
        // attempt=1 → base * 2
        [InlineData(1, 1, 30, 2000)]
        // attempt=2 → base * 4
        [InlineData(2, 1, 30, 4000)]
        // attempt=4 → base * 16
        [InlineData(4, 1, 30, 16000)]
        // attempt=5 → base * 32 = 32000 超过 cap=30000，封顶
        [InlineData(5, 1, 30, 30000)]
        // attempt=10 → 早已封顶，不随 attempt 增长
        [InlineData(10, 1, 30, 30000)]
        public void GetDelayMs_BaseSequence_MatchesExpectedWithoutJitter(int attempt, int baseSeconds, int maxSeconds, int expectedRawMs)
        {
            // 抖动用固定种子，期望结果在 [expectedRaw * 0.8, expectedRaw * 1.2] 区间
            int delay = ReconnectBackoff.GetDelayMs(attempt, baseSeconds, maxSeconds, NewSeeded);
            Assert.InRange(delay, (int)(expectedRawMs * 0.8), (int)(expectedRawMs * 1.2));
        }

        [Fact]
        public void GetDelayMs_Capped_MaxUsedWhenExceeded()
        {
            // attempt=5, base=1, cap=30 → raw=32000 超过 cap → 封顶 30000
            int delay = ReconnectBackoff.GetDelayMs(5, 1, 30, NewSeeded);
            Assert.InRange(delay, 30000, 36000);   // cap * 1.0 ~ cap * 1.2
        }

        [Fact]
        public void GetDelayMs_NonPositiveAttempt_ClampedToZero()
        {
            int delay = ReconnectBackoff.GetDelayMs(-1, 1, 30, NewSeeded);
            // -1 被归一成 0，等价于 attempt=0 → base=1s
            Assert.InRange(delay, 800, 1200);
        }

        [Fact]
        public void GetDelayMs_MaxLessThanBase_MaxAlignsToBase()
        {
            // maxSeconds=1 小于 base=5 → cap 归一成 base=5 → attempt=0 → 5000ms（±20%）
            int delay = ReconnectBackoff.GetDelayMs(0, 5, 1, NewSeeded);
            Assert.InRange(delay, 4000, 6000);
        }

        [Fact]
        public void GetDelayMs_AllZeros_ClampsToMinimum()
        {
            // baseSeconds=0、maxSeconds=0 → 都被归一成 1 → attempt=0 → 1000ms ±20%
            int delay = ReconnectBackoff.GetDelayMs(0, 0, 0, NewSeeded);
            Assert.InRange(delay, 800, 1200);
        }
    }
}
