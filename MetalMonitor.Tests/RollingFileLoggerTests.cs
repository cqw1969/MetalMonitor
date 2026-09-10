using MetalMonitor.Core;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// RollingFileLogger（滚动文件日志器）单元测试
    ///
    /// 覆盖规则：
    ///   1. 单文件达到 maxFileBytes（默认 5MB）后以日期时间命名封存并开新文件；
    ///   2. 文件数超过 maxFileCount（默认 10）时删除最早的 filesToDeleteOnOverflow（默认 5）个；
    ///   3. 多线程并发写入不丢内容、不抛异常。
    /// 测试通过构造参数把阈值调小，避免真的写 5MB。
    /// </summary>
    public class RollingFileLoggerTests : IDisposable
    {
        private readonly string _tempDir;

        public RollingFileLoggerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetalMonitorLogTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* 临时文件清理失败不影响测试结果 */ }
        }

        private string[] LogFiles() => Directory.GetFiles(_tempDir, "*.log");

        private string ReadAllLogs()
        {
            var sb = new StringBuilder();
            foreach (var f in LogFiles())
                sb.AppendLine(File.ReadAllText(f));
            return sb.ToString();
        }

        [Fact]
        public void Log_FirstWrite_CreatesSingleFileContainingContent()
        {
            var logger = new RollingFileLogger(_tempDir);

            logger.Log("测试来源", "hello world 你好");

            var files = LogFiles();
            Assert.Single(files);
            var text = File.ReadAllText(files[0]);
            Assert.Contains("hello world 你好", text);
            Assert.Contains("测试来源", text);
        }

        [Fact]
        public void Log_ExceedingMaxFileSize_RollsToDateNamedNewFile()
        {
            // 阈值仅 200 字节，每条消息约 70+ 字节，写 10 条必然触发多次滚动
            var logger = new RollingFileLogger(_tempDir, maxFileBytes: 200);

            for (int i = 0; i < 10; i++)
                logger.Log("S", $"message number {i} padding-padding-padding");

            var files = LogFiles();
            Assert.True(files.Length >= 2, $"应滚动产生多个文件，实际 {files.Length} 个");

            // 封存文件以当日日期命名
            var today = System.DateTime.Now.ToString("yyyy-MM-dd");
            Assert.All(files, f => Assert.Contains(today, Path.GetFileName(f)));
        }

        [Fact]
        public void Cleanup_FileCountExceedsMax_DeletesOldestAndKeepsNewest()
        {
            // 上限 4 个、超限删 2 个；阈值 60 字节使每条消息都触发滚动
            var logger = new RollingFileLogger(_tempDir,
                maxFileBytes: 60, maxFileCount: 4, filesToDeleteOnOverflow: 2);

            for (int i = 0; i < 12; i++)
                logger.Log("S", $"msg-{i} padding-padding-padding-padding");

            var files = LogFiles();
            Assert.True(files.Length <= 4, $"文件数应被清理到上限内，实际 {files.Length} 个");

            // 最新写入的内容必须还在（清理不能误删当前文件）
            Assert.Contains("msg-11", ReadAllLogs());
        }

        [Fact]
        public void Ctor_ExistingFilesOverLimit_CleansUpOnStartup()
        {
            // 预置 12 个历史日志文件（文件名时间固定为 2020 年，属于"最早"的一批）
            for (int i = 1; i <= 12; i++)
                File.WriteAllText(Path.Combine(_tempDir, $"app_2020-01-01_00-00-{i:00}.log"), "old");

            var logger = new RollingFileLogger(_tempDir, maxFileCount: 10, filesToDeleteOnOverflow: 5);

            var files = LogFiles();
            // 12 个超限 → 删除最早 5 个 → 剩 7 个
            Assert.Equal(7, files.Length);
        }

        [Fact]
        public void Log_ParallelWrites_DoesNotThrowAndLosesNoContent()
        {
            var logger = new RollingFileLogger(_tempDir, maxFileBytes: 1024 * 1024);

            Parallel.For(0, 200, i => logger.Log("P", $"parallel message {i}"));

            var all = ReadAllLogs();
            for (int i = 0; i < 200; i++)
                Assert.Contains($"parallel message {i}", all);
        }

        [Fact]
        public void Log_Exception_IncludesExceptionTypeAndMessage()
        {
            var logger = new RollingFileLogger(_tempDir);

            try
            {
                throw new InvalidOperationException("boom message");
            }
            catch (System.Exception ex)
            {
                logger.Log("UI线程未处理异常", ex);
            }

            var text = ReadAllLogs();
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("boom message", text);
            Assert.Contains("UI线程未处理异常", text);
        }
    }
}
