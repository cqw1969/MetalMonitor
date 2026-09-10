using System.Text;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 滚动文件日志器（运行时异常/事件落盘）：
    /// - 单个日志文件达到 <paramref name="maxFileBytes"/>（默认 5MB）后封存，
    ///   以滚动发生时的日期时间命名，并自动开启新文件继续写入；
    /// - 日志文件数量超过 <paramref name="maxFileCount"/>（默认 10）时，
    ///   删除最早的 <paramref name="filesToDeleteOnOverflow"/>（默认 5）个；
    /// - 全部方法线程安全（lock 串行化写入/滚动/清理）；
    /// - 日志器内部异常永不向外抛出——它运行在全局异常处理路径上，
    ///   二次崩溃会让原始异常被吞掉，故内部失败静默处理。
    /// </summary>
    public class RollingFileLogger
    {
        public const long DefaultMaxFileBytes = 5L * 1024 * 1024; // 5MB
        public const int DefaultMaxFileCount = 10;
        public const int DefaultFilesToDeleteOnOverflow = 5;

        private readonly string _directory;
        private readonly long _maxFileBytes;
        private readonly int _maxFileCount;
        private readonly int _filesToDeleteOnOverflow;
        private readonly string _filePrefix;
        private readonly object _ioLock = new();
        private string? _currentFile;

        public RollingFileLogger(
            string? directory = null,
            long maxFileBytes = DefaultMaxFileBytes,
            int maxFileCount = DefaultMaxFileCount,
            int filesToDeleteOnOverflow = DefaultFilesToDeleteOnOverflow,
            string filePrefix = "app")
        {
            _directory = directory ?? DefaultLogDirectory();
            _maxFileBytes = maxFileBytes;
            _maxFileCount = maxFileCount;
            _filesToDeleteOnOverflow = filesToDeleteOnOverflow;
            _filePrefix = filePrefix;

            Directory.CreateDirectory(_directory);
            // 启动时也执行一次清理，防止上次运行遗留文件已超限
            CleanupOldFiles();
        }

        /// <summary>
        /// 日志目录：开发阶段写入解决方案目录下 logs/，部署阶段回退到程序运行目录
        /// </summary>
        private static string DefaultLogDirectory()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var root = dir;
            while (!string.IsNullOrEmpty(root) && !File.Exists(Path.Combine(root, "MetalMonitor.sln")))
                root = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(root)) root = dir;
            return Path.Combine(root, "logs");
        }

        public void Log(string source, string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {message}{Environment.NewLine}";
                var incomingBytes = Encoding.UTF8.GetByteCount(line);

                lock (_ioLock)
                {
                    EnsureWritable(incomingBytes);
                    File.AppendAllText(_currentFile!, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不得影响主程序与全局异常处理流程
            }
        }

        public void Log(string source, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{ex.GetType().FullName}: {ex.Message}");
            sb.AppendLine("堆栈跟踪:");
            sb.AppendLine(ex.StackTrace ?? "(无堆栈信息)");
            Log(source, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// 确保当前文件存在且剩余容量足够容纳本次写入；
        /// 启动/首次写入时复用最新的未满文件，放不下则滚动到新文件并清理旧文件
        /// </summary>
        private void EnsureWritable(int incomingBytes)
        {
            if (_currentFile == null)
            {
                // 复用最新的尚未写满的日志文件，避免每次启动都新建文件
                _currentFile = FindLatestWritableFile() ?? CreateNewFile();
            }

            long currentSize = File.Exists(_currentFile) ? new FileInfo(_currentFile).Length : 0;
            if (currentSize + incomingBytes < _maxFileBytes)
                return;

            _currentFile = CreateNewFile();
            CleanupOldFiles();
        }

        // 按文件名（含定宽时间戳，字典序即时间序）找最新的未满文件
        private string? FindLatestWritableFile()
        {
            try
            {
                return Directory.GetFiles(_directory, $"{_filePrefix}_*.log")
                    .Where(f => new FileInfo(f).Length < _maxFileBytes)
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 以当前日期时间命名创建新日志文件；同一秒内多次滚动追加序号避免重名覆盖
        /// </summary>
        private string CreateNewFile()
        {
            for (int i = 0; ; i++)
            {
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var name = i == 0
                    ? $"{_filePrefix}_{stamp}.log"
                    : $"{_filePrefix}_{stamp}_{i + 1}.log";
                var path = Path.Combine(_directory, name);
                if (!File.Exists(path))
                {
                    using (File.Create(path)) { }
                    return path;
                }
            }
        }

        /// <summary>
        /// 文件数超过上限时，按文件名（含定宽时间戳，字典序即时间序）删除最早的若干个。
        /// 当前文件按命名时间一定是最新的，不会被删。
        /// </summary>
        private void CleanupOldFiles()
        {
            try
            {
                var files = Directory.GetFiles(_directory, $"{_filePrefix}_*.log")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count <= _maxFileCount) return;

                // 至少保留 1 个文件，防止极端参数下删空
                int toDelete = Math.Min(_filesToDeleteOnOverflow, files.Count - 1);
                for (int i = 0; i < toDelete; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { /* 单个文件删除失败不阻断清理 */ }
                }
            }
            catch
            {
                // 清理失败不影响写入
            }
        }
    }
}
