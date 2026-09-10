using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.UI.ViewModels
{
    public class SystemSettingsViewModel : INotifyPropertyChanged
    {
        private readonly IDatabaseService _databaseService;
        private readonly IDeviceService _deviceService;

        public ObservableCollection<MetricConfig> MetricConfigs { get; set; } = new();

        // 报警方向选项
        public AlarmDirection[] AlarmDirectionOptions { get; } = new AlarmDirection[]
        {
            AlarmDirection.Above,
            AlarmDirection.Below
        };

        private MetricConfig? _selectedMetricConfig;
        public MetricConfig? SelectedMetricConfig
        {
            get => _selectedMetricConfig;
            set
            {
                _selectedMetricConfig = value;
                if (value != null)
                {
                    EditName = value.Name;
                    EditDisplayName = value.DisplayName;
                    EditUnit = value.Unit;
                    EditAlarmThreshold = value.AlarmThreshold;
                    EditAlarmEnabled = value.AlarmEnabled;
                    EditAlarmDirection = value.AlarmDirection;
                    EditMinValue = value.MinValue;
                    EditMaxValue = value.MaxValue;
                    EditIsActive = value.IsActive;
                }
                else
                {
                    // 取消选中（点击列表空白区域）：编辑栏恢复为未选中的空白状态
                    EditName = string.Empty;
                    EditDisplayName = string.Empty;
                    EditUnit = string.Empty;
                    EditAlarmThreshold = 0;
                    EditAlarmEnabled = true;
                    EditAlarmDirection = AlarmDirection.Above;
                    EditMinValue = 0;
                    EditMaxValue = 100;
                    EditIsActive = true;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedConfig));
            }
        }

        // 是否已选中已存在的指标（控制修改/删除按钮可用性）
        public bool HasSelectedConfig => _selectedMetricConfig != null;

        // 编辑表单字段
        private string _editName = string.Empty;
        public string EditName
        {
            get => _editName;
            set => SetProperty(ref _editName, value);
        }

        private string _editDisplayName = string.Empty;
        public string EditDisplayName
        {
            get => _editDisplayName;
            set => SetProperty(ref _editDisplayName, value);
        }

        private string _editUnit = string.Empty;
        public string EditUnit
        {
            get => _editUnit;
            set => SetProperty(ref _editUnit, value);
        }

        private double _editAlarmThreshold;
        public double EditAlarmThreshold
        {
            get => _editAlarmThreshold;
            set => SetProperty(ref _editAlarmThreshold, value);
        }

        private bool _editAlarmEnabled = true;
        public bool EditAlarmEnabled
        {
            get => _editAlarmEnabled;
            set => SetProperty(ref _editAlarmEnabled, value);
        }

        private AlarmDirection _editAlarmDirection = AlarmDirection.Above;
        public AlarmDirection EditAlarmDirection
        {
            get => _editAlarmDirection;
            set => SetProperty(ref _editAlarmDirection, value);
        }

        private double _editMinValue;
        public double EditMinValue
        {
            get => _editMinValue;
            set => SetProperty(ref _editMinValue, value);
        }

        private double _editMaxValue = 100;
        public double EditMaxValue
        {
            get => _editMaxValue;
            set => SetProperty(ref _editMaxValue, value);
        }

        private bool _editIsActive = true;
        public bool EditIsActive
        {
            get => _editIsActive;
            set => SetProperty(ref _editIsActive, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SystemSettingsViewModel(IDatabaseService databaseService, IDeviceService deviceService)
        {
            _databaseService = databaseService;
            _deviceService = deviceService;
        }

        public async Task LoadMetricConfigsAsync()
        {
            var configs = await _databaseService.GetAllMetricConfigsAsync();
            MetricConfigs.Clear();
            foreach (var c in configs)
            {
                MetricConfigs.Add(c);
            }
            StatusMessage = $"已加载 {configs.Count} 个指标配置";
        }

        public async Task AddMetricConfigAsync()
        {
            if (string.IsNullOrWhiteSpace(EditName))
            {
                StatusMessage = "指标名称不能为空";
                return;
            }

            // 检查名称是否重复
            var existing = await _databaseService.GetAllMetricConfigsAsync();
            if (existing.Any(c => c.Name.Equals(EditName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"指标 [{EditName}] 已存在，请使用其他名称";
                return;
            }

            var config = new MetricConfig
            {
                Name = EditName,
                DisplayName = string.IsNullOrWhiteSpace(EditDisplayName) ? EditName : EditDisplayName,
                Unit = EditUnit,
                AlarmThreshold = EditAlarmThreshold,
                AlarmEnabled = EditAlarmEnabled,
                AlarmDirection = EditAlarmDirection,
                MinValue = EditMinValue,
                MaxValue = EditMaxValue,
                IsActive = EditIsActive
            };

            await _databaseService.AddMetricConfigAsync(config);
            await LoadMetricConfigsAsync();
            // 立即同步设备服务的指标配置和报警阈值，使主界面定时器下次刷新即生效
            await _deviceService.ReloadMetricConfigsAsync();
            ClearEditForm();
            StatusMessage = $"指标 [{config.Name}] 添加成功";
        }

        public async Task UpdateMetricConfigAsync()
        {
            if (SelectedMetricConfig == null)
            {
                StatusMessage = "请先选择要修改的指标";
                return;
            }

            if (string.IsNullOrWhiteSpace(EditName))
            {
                StatusMessage = "指标名称不能为空";
                return;
            }

            // 检查名称是否与其他指标重复（排除自身）
            var existing = await _databaseService.GetAllMetricConfigsAsync();
            if (existing.Any(c => c.Id != SelectedMetricConfig.Id && c.Name.Equals(EditName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"指标 [{EditName}] 已存在，请使用其他名称";
                return;
            }

            SelectedMetricConfig.Name = EditName;
            SelectedMetricConfig.DisplayName = string.IsNullOrWhiteSpace(EditDisplayName) ? EditName : EditDisplayName;
            SelectedMetricConfig.Unit = EditUnit;
            SelectedMetricConfig.AlarmThreshold = EditAlarmThreshold;
            SelectedMetricConfig.AlarmEnabled = EditAlarmEnabled;
            SelectedMetricConfig.AlarmDirection = EditAlarmDirection;
            SelectedMetricConfig.MinValue = EditMinValue;
            SelectedMetricConfig.MaxValue = EditMaxValue;
            SelectedMetricConfig.IsActive = EditIsActive;

            var updatedName = SelectedMetricConfig.Name;
            await _databaseService.UpdateMetricConfigAsync(SelectedMetricConfig);
            await LoadMetricConfigsAsync();
            // 同步设备服务的指标配置和报警阈值，使变更立即生效
            await _deviceService.ReloadMetricConfigsAsync();
            StatusMessage = $"指标 [{updatedName}] 修改成功";
        }

        public async Task DeleteMetricConfigAsync()
        {
            if (SelectedMetricConfig == null)
            {
                StatusMessage = "请先选择要删除的指标";
                return;
            }

            var configName = SelectedMetricConfig.Name;
            await _databaseService.DeleteMetricConfigAsync(SelectedMetricConfig.Id);
            await LoadMetricConfigsAsync();
            // 同步设备服务的指标配置和报警阈值，使删除立即生效
            await _deviceService.ReloadMetricConfigsAsync();
            ClearEditForm();
            StatusMessage = $"指标 [{configName}] 已删除";
        }

        public async Task ReloadServiceConfigsAsync()
        {
            // 重新加载设备服务的指标配置和报警阈值
            await _deviceService.ReloadMetricConfigsAsync();
        }

        private void ClearEditForm()
        {
            EditName = string.Empty;
            EditDisplayName = string.Empty;
            EditUnit = string.Empty;
            EditAlarmThreshold = 0;
            EditAlarmEnabled = true;
            EditAlarmDirection = AlarmDirection.Above;
            EditMinValue = 0;
            EditMaxValue = 100;
            EditIsActive = true;
            SelectedMetricConfig = null;
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
