# MetalMonitor

工业设备温湿度监控系统：STM32 固件采集传感器数据，通过自定义串口协议上传到 .NET WPF 上位机，上位机完成解析、展示、存储与告警。

## 功能特性

- **固件端**：STM32F103 采集 DS18B20 温度与 ADC 电压，按自定义 AA55 帧格式封装，USART 115200 波特率上报，含 CRC16 校验
- **流式帧解析**：基于环形缓冲区 + 状态机的字节流解析，正确处理粘包 / 半包，坏帧丢弃后自动重新同步
- **链路可靠性**：最后有效帧时间戳 + 看门狗判定断线；指数退避自动重连，真机拔插后无需重启上位机即可恢复
- **数据持久化**：采集数据缓冲后批量写入 SQLite，启用 WAL 模式避免日志无限增长
- **实时展示**：WPF 界面实时曲线、设备卡片、告警提示，后台采集线程与 UI 线程解耦
- **测试覆盖**：帧解析、链路健康判定、指数退避、缓冲写入等核心逻辑均有 xUnit 单元测试

## 技术栈

| 层 | 技术 |
|---|---|
| 上位机 UI | .NET 8 / WPF |
| 图表 | LiveChartsCore |
| 通信 | System.IO.Ports（串口）、MQTTnet |
| 存储 | Microsoft.Data.Sqlite（SQLite） |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 固件 | STM32F103 / 标准外设库 / Keil MDK |
| 测试 | xUnit |

## 项目结构

```
MetalMonitor/
├── MetalMonitor.UI/              # WPF 上位机（入口）
├── MetalMonitor.core/            # 核心业务：帧解析、设备服务、串口、MQTT、存储
├── MetalMonitor.Tests/           # xUnit 单元测试
├── MetalMonitor.MqttPublisher/   # MQTT 模拟数据发布工具（调试用）
├── tools/Crc16ModbusTest/        # CRC16 校验向量验证工具
├── firmware/                     # STM32 固件源码（Keil 工程）
├── docs/protocol/PROTOCOL.md     # 通信协议文档
└── config.json                   # 运行配置
```

## 快速开始

### 环境要求

- .NET 8 SDK
- Windows（WPF 仅限 Windows）
- （可选）Keil MDK 用于编译固件

### 运行上位机

```bash
dotnet run --project MetalMonitor.UI
```

首次运行会读取 `config.json`，可修改串口号、数据库路径、采样间隔等。

### 编译固件

用 Keil MDK 打开 `firmware/stm32_f103_metalmonitor/Project/Fire_F103.uvprojx`，编译后烧录到 STM32F103。

### 运行测试

```bash
dotnet test
```

## 通信协议

固件与上位机之间使用自定义帧协议，详见 [docs/protocol/PROTOCOL.md](docs/protocol/PROTOCOL.md)。

帧格式概览：`AA 55 | NODE_ID | LEN | CMD | PAYLOAD | CRC16`，当前 CMD 0x01 为上行传感器数据帧，1 Hz 上报。

## 配置说明

`config.json` 主要字段：

| 字段 | 说明 |
|---|---|
| `DatabasePath` | SQLite 数据库目录 |
| `Serial.PortName` | 默认串口号 |
| `Serial.WatchdogSeconds` | 断线判定超时（秒） |
| `Serial.ReconnectBaseSeconds` / `ReconnectMaxSeconds` | 重连退避区间 |
