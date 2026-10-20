using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 配置中心：config.json 的唯一读写入口。
    /// 设计模型：内存持有配置副本，属性 setter =「改内存 + 立即落盘」；
    /// 本类可被多处各自 new（DeviceService/DatabaseService），各实例持独立副本互不感知，
    /// 跨实例同步靠重新读文件（Reload）而非共享对象。
    /// </summary>
    public class ConfigService
    {
        /// <summary>配置文件 config.json 的完整绝对路径，构造时确定后不再改变。</summary>
        private readonly string _configPath;
        /// <summary>配置在内存中的持有副本；getter 读它，setter 改它并立即落盘。</summary>
        private AppConfig _config = new();

        // JsonSerializerOptions 的类型序列化元数据（反射编译产物）缓存在实例内部，
        // 每次 new 都会重建一遍——固定选项缓存为静态实例复用即可
        /// <summary>写盘专用的静态 JSON 序列化选项（缩进输出），跨实例复用以避免重复构建序列化元数据。</summary>
        private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

        /// <summary>
        /// 数据库文件所在目录（不含文件名），取自 config.json；为空时由使用方回退到默认目录。
        /// setter 采用写穿策略：修改内存副本后立即持久化到磁盘。
        /// </summary>
        public string DatabasePath
        {
            get => _config.DatabasePath;
            set
            {
                _config.DatabasePath = value;
                SaveConfig();
            }
        }

        /// <summary>
        /// 批量落库触发条数，默认 50；缓冲记录数达到该值时执行一次单事务批量写入。
        /// setter 改内存并立即写盘。
        /// </summary>
        public int BatchSize
        {
            get => _config.BatchSize;
            set
            {
                _config.BatchSize = value;
                SaveConfig();
            }
        }

        // 数据来源选择（模拟数据/MQTT/串口），跨启动持久化
        /// <summary>
        /// 当前数据来源名称（"模拟数据"/"MQTT"/"串口"），跨启动持久化；
        /// 读取时若配置中为空则回退为"模拟数据"。setter 改内存并立即写盘。
        /// </summary>
        public string DataSource
        {
            get => string.IsNullOrEmpty(_config.DataSource) ? "模拟数据" : _config.DataSource;
            set
            {
                _config.DataSource = value;
                SaveConfig();
            }
        }

        /// <summary>
        /// UI 数据刷新间隔（毫秒），默认 1000。setter 改内存并立即写盘。
        /// </summary>
        public int RefreshInterval
        {
            get => _config.RefreshInterval;
            set
            {
                _config.RefreshInterval = value;
                SaveConfig();
            }
        }

        // ===== 串口接入配置段（T3.4 第④步，2026-10-14 决策）=====
        /// <summary>
        /// 上次成功使用的串口名（如 "COM3"），跨启动持久化，作为下次连接的默认候选；
        /// 空字符串表示尚未持久化，由调用方回退（首可用口/用户手选）。
        /// setter 由串口连接成功后调用，写穿落盘。
        /// </summary>
        public string SerialPortName
        {
            get => _config.Serial?.PortName ?? string.Empty;
            set
            {
                _config.Serial ??= new SerialConfig();
                _config.Serial.PortName = value;
                SaveConfig();
            }
        }

        /// <summary>
        /// NODE_ID → 设备名 路由表的启动只读快照（机制/策略分离：路由是策略，仅此一读）。
        /// 每次调用按当前内存副本新建只读字典，键为 NODE_ID 字节值；
        /// JSON 键为 NODE_ID 的十六进制字符串（"2A"/"2a" 均可），非法键静默忽略（路由表损坏不挡启动）；
        /// 0x00（单节点缺省）不入表，由调用方按"取第一台"处理（见 PROTOCOL.md §2.1）。
        /// 设计为只读：程序内无写入通道，仅手工编辑 config.json，系统启动时读取一次，运行中不变。
        /// </summary>
        /// <returns>NODE_ID → 设备名 的只读快照；无路由段时为空字典。</returns>
        public IReadOnlyDictionary<byte, string> GetNodeRoutingSnapshot()
        {
            var snapshot = new Dictionary<byte, string>();
            var routing = _config.Serial?.NodeRouting;
            if (routing == null)
                return snapshot;

            foreach (var kv in routing)
            {
                if (byte.TryParse(kv.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nodeId))
                    snapshot[nodeId] = kv.Value;
                // 非法键（如 "GG"、带 0x 前缀）静默忽略并跳过
            }
            return snapshot;
        }

        /// <summary>
        /// 以自动定位的 config.json 路径初始化配置服务，并立即从磁盘加载配置。
        /// </summary>
        /// <remarks>定位规则见 <see cref="FindConfigPath"/>：开发期取解决方案目录，部署期取程序运行目录。</remarks>
        public ConfigService() : this(FindConfigPath())
        {
        }

        // 显式指定配置文件路径（便于单元测试隔离与自定义部署目录）
        /// <summary>
        /// 以显式指定的配置文件路径初始化，并立即从该文件加载配置到内存。
        /// </summary>
        /// <param name="configPath">config.json 的完整路径；通常由测试或自定义部署场景提供。</param>
        public ConfigService(string configPath)
        {
            _configPath = configPath;

            LoadConfig();
        }

        // 配置文件定位：从程序目录逐级向上找 .sln（开发阶段配置随解决方案存放，不进 C 盘），
        // 找不到回退到程序运行目录（部署形态）
        /// <summary>
        /// 定位 config.json 的存放目录。
        /// </summary>
        /// <returns>开发阶段返回解决方案根目录下的 config.json；找不到 .sln 时回退到程序运行目录下的 config.json。</returns>
        private static string FindConfigPath()
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var appDirectory = Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            
            var currentDir = new DirectoryInfo(appDirectory);
            while (currentDir != null)
            {
                var slnFiles = currentDir.GetFiles("*.sln");
                if (slnFiles.Length > 0)
                {
                    return Path.Combine(currentDir.FullName, "config.json");
                }
                currentDir = currentDir.Parent;
            }
            
            return Path.Combine(appDirectory, "config.json");
        }

        // 读配置：文件损坏时静默回退默认值（配置坏了不能挡住主程序启动）
        /// <summary>
        /// 从磁盘读取 config.json 填充内存副本。
        /// </summary>
        /// <remarks>
        /// 文件不存在时使用默认配置并立即创建一份新文件；文件存在但读取或反序列化失败时，
        /// 静默回退到默认配置，不向调用方抛异常，避免配置损坏阻断主程序启动。
        /// </remarks>
        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch
                {
                    _config = new AppConfig();
                }
            }
            else
            {
                _config = new AppConfig();
                SaveConfig();
            }
        }

        // 写配置：任何属性 setter 都会走到这里，立即持久化（低频操作，无需防抖）
        /// <summary>
        /// 将内存配置副本以缩进 JSON 立即写入 config.json（写穿策略的落盘环节）。
        /// </summary>
        /// <remarks>写盘为低频操作，不做防抖；任何 IO/序列化异常均静默吞掉，不影响调用方运行。</remarks>
        private void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, s_writeOptions);
                File.WriteAllText(_configPath, json);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 丢弃当前内存副本并重新从 config.json 读取，用于跨实例感知其他实例写入的最新配置。
        /// </summary>
        public void Reload()
        {
            LoadConfig();
        }

        /// <summary>
        /// config.json 的序列化模型（私有嵌套），字段与磁盘 JSON 一一对应。
        /// </summary>
        private class AppConfig
        {
            /// <summary>数据库文件所在目录；空字符串表示未配置，由使用方回退默认目录。</summary>
            public string DatabasePath { get; set; } = string.Empty;
            /// <summary>批量落库触发条数，默认 50。</summary>
            public int BatchSize { get; set; } = 50;
            /// <summary>UI 数据刷新间隔（毫秒），默认 1000。</summary>
            public int RefreshInterval { get; set; } = 1000;
            /// <summary>持久化的数据来源名称；空字符串由 getter 侧解释为"模拟数据"。</summary>
            public string DataSource { get; set; } = string.Empty;
            /// <summary>串口接入配置段（端口持久化 + NODE_ID 路由表）；旧版 config.json 无此段时保持默认实例。</summary>
            public SerialConfig Serial { get; set; } = new();
        }

        /// <summary>
        /// config.json 的 Serial 序列化模型（私有嵌套），字段与磁盘 JSON 一一对应。
        /// </summary>
        private class SerialConfig
        {
            /// <summary>上次成功使用的串口名（如 "COM3"）；空 = 未持久化。</summary>
            public string PortName { get; set; } = string.Empty;
            /// <summary>NODE_ID 路由表：JSON 键 = NODE_ID 十六进制字符串（如 "2A"），值 = 设备名；只读段，程序永不写。</summary>
            public Dictionary<string, string> NodeRouting { get; set; } = new();
        }
    }
}
