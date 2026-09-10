using System.Reflection;
using System.Text.Json;

namespace MetalMonitor.Core
{
    public class ConfigService
    {
        private readonly string _configPath;
        private AppConfig _config = new();

        public string DatabasePath
        {
            get => _config.DatabasePath;
            set
            {
                _config.DatabasePath = value;
                SaveConfig();
            }
        }

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
        public string DataSource
        {
            get => string.IsNullOrEmpty(_config.DataSource) ? "模拟数据" : _config.DataSource;
            set
            {
                _config.DataSource = value;
                SaveConfig();
            }
        }

        public int RefreshInterval
        {
            get => _config.RefreshInterval;
            set
            {
                _config.RefreshInterval = value;
                SaveConfig();
            }
        }

        public ConfigService() : this(FindConfigPath())
        {
        }

        // 显式指定配置文件路径（便于单元测试隔离与自定义部署目录）
        public ConfigService(string configPath)
        {
            _configPath = configPath;

            LoadConfig();
        }

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

        private void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch
            {
            }
        }

        public void Reload()
        {
            LoadConfig();
        }

        private class AppConfig
        {
            public string DatabasePath { get; set; } = string.Empty;
            public int BatchSize { get; set; } = 50;
            public int RefreshInterval { get; set; } = 1000;
            public string DataSource { get; set; } = string.Empty;
        }
    }
}
