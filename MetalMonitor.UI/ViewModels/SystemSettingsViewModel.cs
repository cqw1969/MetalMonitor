using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.UI.ViewModels
{
    /// <summary>
    /// 系统设置窗口视图模型：管理指标配置（名称、显示名、单位、报警阈值/方向/开关、最值、启停）的
    /// 增删改查，变更后同步重载设备服务的配置与报警阈值；选中配置时回填表单，取消选中时清空表单。
    /// </summary>
    public class SystemSettingsViewModel : INotifyPropertyChanged
    {
        /// <summary>数据库服务，负责指标配置的持久化增删改查。</summary>
        private readonly IDatabaseService _databaseService;
        /// <summary>设备服务，用于在配置变更后重载内存中的指标配置与报警阈值。</summary>
        private readonly IDeviceService _deviceService;

        /// <summary>指标配置集合（绑定 DataGrid），每次加载时整体重建。</summary>
        public ObservableCollection<MetricConfig> MetricConfigs { get; set; } = new();

        /// <summary>获取报警方向可选项（高于阈值报警/低于阈值报警），绑定方向下拉框。</summary>
        // 报警方向选项
        public AlarmDirection[] AlarmDirectionOptions { get; } = new AlarmDirection[]
        {
            AlarmDirection.Above,
            AlarmDirection.Below
        };

        /// <summary>DataGrid 当前选中指标配置 backing field；null 表示未选中。</summary>
        private MetricConfig? _selectedMetricConfig;
        /// <summary>
        /// 获取或设置当前选中的指标配置：选中时把配置各字段回填编辑表单，取消选中（null）时恢复为空白默认态。
        /// </summary>
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

        /// <summary>获取是否已选中已存在的指标（控制修改/删除按钮可用性）。</summary>
        // 是否已选中已存在的指标（控制修改/删除按钮可用性）
        public bool HasSelectedConfig => _selectedMetricConfig != null;

        /// <summary>编辑表单-指标名称 backing field。</summary>
        // 编辑表单字段
        private string _editName = string.Empty;
        /// <summary>获取或设置编辑表单中的指标名称（唯一标识，不区分大小写查重）。</summary>
        public string EditName
        {
            get => _editName;
            set => SetProperty(ref _editName, value);
        }

        /// <summary>编辑表单-显示名称 backing field。</summary>
        private string _editDisplayName = string.Empty;
        /// <summary>获取或设置编辑表单中的指标显示名称（留空时新增操作会回退为指标名）。</summary>
        public string EditDisplayName
        {
            get => _editDisplayName;
            set => SetProperty(ref _editDisplayName, value);
        }

        /// <summary>编辑表单-单位 backing field。</summary>
        private string _editUnit = string.Empty;
        /// <summary>获取或设置编辑表单中的指标单位。</summary>
        public string EditUnit
        {
            get => _editUnit;
            set => SetProperty(ref _editUnit, value);
        }

        /// <summary>编辑表单-报警阈值 backing field。</summary>
        private double _editAlarmThreshold;
        /// <summary>获取或设置编辑表单中的报警阈值。</summary>
        public double EditAlarmThreshold
        {
            get => _editAlarmThreshold;
            set => SetProperty(ref _editAlarmThreshold, value);
        }

        /// <summary>编辑表单-报警是否启用 backing field，默认 true。</summary>
        private bool _editAlarmEnabled = true;
        /// <summary>获取或设置编辑表单中的报警启用开关。</summary>
        public bool EditAlarmEnabled
        {
            get => _editAlarmEnabled;
            set => SetProperty(ref _editAlarmEnabled, value);
        }

        /// <summary>编辑表单-报警方向 backing field，默认高于阈值报警。</summary>
        private AlarmDirection _editAlarmDirection = AlarmDirection.Above;
        /// <summary>获取或设置编辑表单中的报警方向（高于/低于阈值）。</summary>
        public AlarmDirection EditAlarmDirection
        {
            get => _editAlarmDirection;
            set => SetProperty(ref _editAlarmDirection, value);
        }

        /// <summary>编辑表单-坐标最小值 backing field。</summary>
        private double _editMinValue;
        /// <summary>获取或设置编辑表单中的指标坐标/量程最小值。</summary>
        public double EditMinValue
        {
            get => _editMinValue;
            set => SetProperty(ref _editMinValue, value);
        }

        /// <summary>编辑表单-坐标最大值 backing field，默认 100。</summary>
        private double _editMaxValue = 100;
        /// <summary>获取或设置编辑表单中的指标坐标/量程最大值。</summary>
        public double EditMaxValue
        {
            get => _editMaxValue;
            set => SetProperty(ref _editMaxValue, value);
        }

        /// <summary>编辑表单-指标是否启用 backing field，默认 true。</summary>
        private bool _editIsActive = true;
        /// <summary>获取或设置编辑表单中的指标启用状态（停用后卡片置灰失效）。</summary>
        public bool EditIsActive
        {
            get => _editIsActive;
            set => SetProperty(ref _editIsActive, value);
        }

        /// <summary>状态栏提示消息 backing field。</summary>
        private string _statusMessage = string.Empty;
        /// <summary>获取或设置状态栏提示消息（加载结果、操作成功/失败原因等）。</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>属性变更通知事件，供 WPF 绑定系统订阅。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 初始化 <see cref="SystemSettingsViewModel"/> 的新实例。
        /// </summary>
        /// <param name="databaseService">数据库服务。</param>
        /// <param name="deviceService">设备数据服务。</param>
        public SystemSettingsViewModel(IDatabaseService databaseService, IDeviceService deviceService)
        {
            _databaseService = databaseService;
            _deviceService = deviceService;
        }

        /// <summary>
        /// 从数据库加载全部指标配置并重建 <see cref="MetricConfigs"/> 集合，同时更新状态栏计数。
        /// </summary>
        /// <returns>表示异步加载操作的任务。</returns>
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

        /// <summary>
        /// 新增指标配置：校验名称非空且不与已有指标重名（忽略大小写）后写入数据库、
        /// 重载列表与设备服务配置，并清空编辑表单。
        /// </summary>
        /// <returns>表示异步新增操作的任务。</returns>
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

        /// <summary>
        /// 修改当前选中指标配置：校验选中态、名称非空且不与其他指标重名后，
        /// 把表单值写回对象并持久化，再重载列表与设备服务配置使变更立即生效。
        /// </summary>
        /// <returns>表示异步修改操作的任务。</returns>
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

        /// <summary>
        /// 删除当前选中指标配置：用户确认后由窗口调用本方法持久化删除，重载列表与设备服务配置并清空编辑表单。
        /// </summary>
        /// <returns>表示异步删除操作的任务。</returns>
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

        /// <summary>
        /// 重新加载设备服务的指标配置与报警阈值（供外部在需要时手动同步）。
        /// </summary>
        /// <returns>表示异步重载操作的任务。</returns>
        public async Task ReloadServiceConfigsAsync()
        {
            // 重新加载设备服务的指标配置和报警阈值
            await _deviceService.ReloadMetricConfigsAsync();
        }

        /// <summary>
        /// 清空编辑表单全部字段为空白默认态，并取消指标配置选中。
        /// </summary>
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

        /// <summary>
        /// 通用属性设置器：值变化时更新 backing field 并触发属性变更通知。
        /// </summary>
        /// <typeparam name="T">属性值类型。</typeparam>
        /// <param name="field">属性 backing field 引用。</param>
        /// <param name="value">期望设置的新值。</param>
        /// <param name="propertyName">属性名称，null 时由 <see cref="CallerMemberNameAttribute"/> 自动传入。</param>
        /// <returns>值发生变化返回 true；新旧值相等未触发通知返回 false。</returns>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件；属性名缺省时由调用方成员名自动填充。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称，null 时由 <see cref="CallerMemberNameAttribute"/> 自动传入。</param>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
