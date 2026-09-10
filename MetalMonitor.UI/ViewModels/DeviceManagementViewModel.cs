using MetalMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MetalMonitor.UI.ViewModels
{
    public class DeviceManagementViewModel : INotifyPropertyChanged
    {
        private readonly IDatabaseService _databaseService;
        private readonly IDeviceService _deviceService;

        public ObservableCollection<Device> Devices { get; set; } = new();

        private Device? _selectedDevice;
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

        // 是否已选中设备（控制修改/删除按钮可用性）
        public bool HasSelectedDevice => _selectedDevice != null;

        // 编辑表单字段
        private string _editName = string.Empty;
        public string EditName
        {
            get => _editName;
            set => SetProperty(ref _editName, value);
        }

        private string _editDeviceType = string.Empty;
        public string EditDeviceType
        {
            get => _editDeviceType;
            set => SetProperty(ref _editDeviceType, value);
        }

        private string _editLocation = string.Empty;
        public string EditLocation
        {
            get => _editLocation;
            set => SetProperty(ref _editLocation, value);
        }

        private string _editDescription = string.Empty;
        public string EditDescription
        {
            get => _editDescription;
            set => SetProperty(ref _editDescription, value);
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

        public DeviceManagementViewModel(IDatabaseService databaseService, IDeviceService deviceService)
        {
            _databaseService = databaseService;
            _deviceService = deviceService;
        }

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

        public async Task ReloadMainDeviceListAsync()
        {
            await _deviceService.ReloadDevicesAsync();
        }

        private void ClearEditForm()
        {
            EditName = string.Empty;
            EditDeviceType = string.Empty;
            EditLocation = string.Empty;
            EditDescription = string.Empty;
            EditIsActive = true;
            SelectedDevice = null;
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
