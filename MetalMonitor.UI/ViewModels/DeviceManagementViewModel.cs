using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MetalMonitor.UI.ViewModels
{
    /// <summary>
    /// 设备管理窗口视图模型：从数据库加载设备列表，支持设备的新增、修改、删除，
    /// 变更后同步刷新设备服务的内存列表；选中设备时回填编辑表单，取消选中时清空表单。
    /// </summary>
    public class DeviceManagementViewModel : INotifyPropertyChanged
    {
        /// <summary>数据库服务，负责设备记录的持久化增删改查。</summary>
        private readonly IDatabaseService _databaseService;
        /// <summary>设备服务，用于在数据库变更后重载主界面使用的内存设备列表。</summary>
        private readonly IDeviceService _deviceService;

        /// <summary>设备列表集合（绑定 DataGrid），每次加载时整体重建。</summary>
        public ObservableCollection<Device> Devices { get; set; } = new();

        /// <summary>DataGrid 当前选中设备 backing field；null 表示未选中。</summary>
        private Device? _selectedDevice;
        /// <summary>
        /// 获取或设置当前选中设备：选中时把设备属性回填编辑表单，取消选中（null）时把表单恢复为空白默认态。
        /// </summary>
        public Device? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                _selectedDevice = value;
                if (value != null)
                {
                    EditName = value.Name;
                    EditDeviceType = value.DeviceType;
                    EditLocation = value.Location;
                    EditDescription = value.Description;
                    EditIsActive = value.IsActive;
                }
                else
                {
                    // 取消选中（点击列表空白区域）：编辑栏恢复为未选中的空白状态
                    EditName = string.Empty;
                    EditDeviceType = string.Empty;
                    EditLocation = string.Empty;
                    EditDescription = string.Empty;
                    EditIsActive = true;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedDevice));
            }
        }

        /// <summary>获取是否已选中设备（控制修改/删除按钮可用性）。</summary>
        public bool HasSelectedDevice => _selectedDevice != null;

        /// <summary>编辑表单-设备名称 backing field。</summary>
        private string _editName = string.Empty;
        /// <summary>获取或设置编辑表单中的设备名称。</summary>
        public string EditName
        {
            get => _editName;
            set => SetProperty(ref _editName, value);
        }

        /// <summary>编辑表单-设备类型 backing field。</summary>
        private string _editDeviceType = string.Empty;
        /// <summary>获取或设置编辑表单中的设备类型。</summary>
        public string EditDeviceType
        {
            get => _editDeviceType;
            set => SetProperty(ref _editDeviceType, value);
        }

        /// <summary>编辑表单-安装位置 backing field。</summary>
        private string _editLocation = string.Empty;
        /// <summary>获取或设置编辑表单中的安装位置。</summary>
        public string EditLocation
        {
            get => _editLocation;
            set => SetProperty(ref _editLocation, value);
        }

        /// <summary>编辑表单-描述 backing field。</summary>
        private string _editDescription = string.Empty;
        /// <summary>获取或设置编辑表单中的设备描述。</summary>
        public string EditDescription
        {
            get => _editDescription;
            set => SetProperty(ref _editDescription, value);
        }

        /// <summary>编辑表单-是否启用 backing field，默认 true。</summary>
        private bool _editIsActive = true;
        /// <summary>获取或设置编辑表单中的设备启用状态。</summary>
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
        /// 初始化 <see cref="DeviceManagementViewModel"/> 的新实例。
        /// </summary>
        /// <param name="databaseService">数据库服务。</param>
        /// <param name="deviceService">设备数据服务。</param>
        public DeviceManagementViewModel(IDatabaseService databaseService, IDeviceService deviceService)
        {
            _databaseService = databaseService;
            _deviceService = deviceService;
        }

        /// <summary>
        /// 从数据库加载全部设备并重建 <see cref="Devices"/> 集合，同时更新状态栏计数。
        /// </summary>
        /// <returns>表示异步加载操作的任务。</returns>
        public async Task LoadDevicesAsync()
        {
            var devices = await _databaseService.GetAllDevicesAsync();
            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(d);
            }
            StatusMessage = $"已加载 {devices.Count} 台设备";
        }

        /// <summary>
        /// 新增设备：校验名称非空后写入数据库、重载列表与设备服务内存列表，并清空编辑表单。
        /// </summary>
        /// <returns>表示异步新增操作的任务。</returns>
        public async Task AddDeviceAsync()
        {
            if (string.IsNullOrWhiteSpace(EditName))
            {
                StatusMessage = "设备名称不能为空";
                return;
            }

            var device = new Device(EditName, EditDeviceType, EditLocation, EditDescription);
            await _databaseService.AddDeviceAsync(device);
            await LoadDevicesAsync();
            // 立即同步主界面设备列表，使定时器下次刷新即可添加新卡片
            await _deviceService.ReloadDevicesAsync();
            ClearEditForm();
            StatusMessage = $"设备 [{device.Name}] 添加成功";
        }

        /// <summary>
        /// 修改当前选中设备：校验选中态与名称后把表单值写回对象并持久化，
        /// 重载列表与设备服务，再按 Id 重新选中刚修改的设备以保持选中态与表单内容。
        /// </summary>
        /// <returns>表示异步修改操作的任务。</returns>
        public async Task UpdateDeviceAsync()
        {
            if (SelectedDevice == null)
            {
                StatusMessage = "请先选择要修改的设备";
                return;
            }

            if (string.IsNullOrWhiteSpace(EditName))
            {
                StatusMessage = "设备名称不能为空";
                return;
            }

            // 提前捕获引用与名称：后续 LoadDevicesAsync 会 Clear() 集合，
            // DataGrid 的 SelectedItem 会随之被置空，导致 SelectedDevice 变为 null
            var deviceToUpdate = SelectedDevice;
            var updatedName = EditName;

            deviceToUpdate.Name = EditName;
            deviceToUpdate.DeviceType = EditDeviceType;
            deviceToUpdate.Location = EditLocation;
            deviceToUpdate.Description = EditDescription;
            deviceToUpdate.IsActive = EditIsActive;

            await _databaseService.UpdateDeviceAsync(deviceToUpdate);
            await LoadDevicesAsync();
            // 同步主界面设备列表，使设备属性变更立即生效
            await _deviceService.ReloadDevicesAsync();

            // LoadDevicesAsync 重建了集合（元素是新对象），按 Id 重新选中刚修改的设备，保持选中态与编辑表单
            var refreshed = Devices.FirstOrDefault(d => d.Id == deviceToUpdate.Id);
            if (refreshed != null)
            {
                SelectedDevice = refreshed;
            }

            StatusMessage = $"设备 [{updatedName}] 修改成功";
        }

        /// <summary>
        /// 删除当前选中设备：持久化删除后重载列表与设备服务内存列表，并清空编辑表单。
        /// </summary>
        /// <returns>表示异步删除操作的任务。</returns>
        public async Task DeleteDeviceAsync()
        {
            if (SelectedDevice == null)
            {
                StatusMessage = "请先选择要删除的设备";
                return;
            }

            var deviceName = SelectedDevice.Name;
            await _databaseService.DeleteDeviceAsync(SelectedDevice.Id);
            await LoadDevicesAsync();
            // 同步主界面设备列表，使定时器下次刷新即可移除已删除设备的卡片
            await _deviceService.ReloadDevicesAsync();
            ClearEditForm();
            StatusMessage = $"设备 [{deviceName}] 已删除";
        }

        /// <summary>
        /// 通知设备服务重新加载设备内存列表，使主界面与管理窗口数据保持一致。
        /// </summary>
        /// <returns>表示异步重载操作的任务。</returns>
        public async Task ReloadMainDeviceListAsync()
        {
            await _deviceService.ReloadDevicesAsync();
        }

        /// <summary>
        /// 清空编辑表单全部字段为空白默认态，并取消设备选中。
        /// </summary>
        private void ClearEditForm()
        {
            EditName = string.Empty;
            EditDeviceType = string.Empty;
            EditLocation = string.Empty;
            EditDescription = string.Empty;
            EditIsActive = true;
            SelectedDevice = null;
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
