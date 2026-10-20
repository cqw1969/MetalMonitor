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
        /// <summary>默认单文件大小上限：5 MB，达到后滚动到新文件。</summary>
        public const long DefaultMaxFileBytes = 5L * 1024 * 1024; // 5MB
        /// <summary>默认保留的日志文件数量上限：10 个。</summary>
        public const int DefaultMaxFileCount = 10;
        /// <summary>默认超限时一次删除的最早文件个数：5 个。</summary>
        public const int DefaultFilesToDeleteOnOverflow = 5;

        /// <summary>日志文件所在目录（启动时确保存在）。</summary>
        private readonly string _directory;
        /// <summary>单个日志文件的字节数上限，达到后触发滚动。</summary>
        private readonly long _maxFileBytes;
        /// <summary>保留日志文件的数量上限，超过即触发清理。</summary>
        private readonly int _maxFileCount;
        /// <summary>文件数超限时每次清理删除的最早文件个数。</summary>
        private readonly int _filesToDeleteOnOverflow;
        /// <summary>日志文件名前缀（最终命名形如 {前缀}_yyyy-MM-dd_HH-mm-ss.log）。</summary>
        private readonly string _filePrefix;
        /// <summary>串行化所有文件写入、滚动与清理的同步锁，保证多线程调用安全。</summary>
        private readonly object _ioLock = new();
        /// <summary>当前正在追加写入的日志文件完整路径；首次写入时延迟确定。</summary>
        private string? _currentFile;

        /// <summary>
        /// 创建滚动文件日志器并准备日志目录。
        /// </summary>
        /// <param name="directory">日志目录；为 null 时按 <see cref="DefaultLogDirectory"/> 自动定位。</param>
        /// <param name="maxFileBytes">单文件字节数上限，默认 5 MB。</param>
        /// <param name="maxFileCount">保留文件数量上限，默认 10。</param>
        /// <param name="filesToDeleteOnOverflow">超限时每次删除的最早文件个数，默认 5。</param>
        /// <param name="filePrefix">日志文件名前缀，默认 "app"。</param>
        /// <remarks>构造时立即创建目录并执行一次旧文件清理，防止上次运行遗留文件已超限。</remarks>
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
        /// <returns>定位到的默认日志目录完整路径（解决方案根目录或程序基目录下的 logs）。</returns>
        private static string DefaultLogDirectory()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var root = dir;
            while (!string.IsNullOrEmpty(root) && !File.Exists(Path.Combine(root, "MetalMonitor.sln")))
                root = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(root)) root = dir;
            return Path.Combine(root, "logs");
        }

        /// <summary>
        /// 追加一行文本日志（自动加时间戳与来源前缀），必要时先滚动文件再写入。
        /// </summary>
        /// <param name="source">日志来源标识（如模块/类名），写入行的第二个方括号段。</param>
        /// <param name="message">日志正文。</param>
        /// <remarks>容量检查与追加写入在 <see cref="_ioLock"/> 内串行完成；任何异常静默吞掉，不影响主程序。</remarks>
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

        /// <summary>
        /// 将异常的类型、消息与堆栈跟踪格式化为多行文本后写入日志。
        /// </summary>
        /// <param name="source">日志来源标识。</param>
        /// <param name="ex">待记录的异常；堆栈为空时以"(无堆栈信息)"占位。</param>
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
        /// <param name="incomingBytes">本次待写入内容的 UTF-8 字节数。</param>
        /// <remarks>调用方必须已持有 <see cref="_ioLock"/>。</remarks>
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
        /// <summary>
        /// 查找最新的尚未写满的日志文件，供启动/首次写入时复用。
        /// </summary>
        /// <returns>按文件名倒序（字典序即时间序）取第一个小于大小上限的文件完整路径；不存在或读取失败时返回 null。</returns>
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
        /// <returns>新建空日志文件的完整路径。</returns>
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
