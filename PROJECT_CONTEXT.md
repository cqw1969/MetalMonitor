# PROJECT_CONTEXT.md — AI 会话上下文持久化文件

> **本文件是 AI 会话的"引导扇区"**：任何新会话或模型切换后，AI 读取此文件即可恢复全部项目上下文，无需用户重新输入历史。

---

## AI 启动协议（机器可执行指令）

```
1. 读取本文件全部内容。
2. 读取 PROJECT_PLAN.md 的 §0（项目锚点）和 §B（当前周任务表）。
3. 读取 error_fix_log.md 的"经验教训总结"表（底部）。
4. 检查 PROJECT_PLAN.md §C1（每日日志）最后一条，确认当前进度位置。
5. 根据"当前进度指针"段，定位用户正在做的任务。
6. 根据"开放问题"段，了解未解决的疑问。
7. 开始协助用户时，先用一句话复述项目当前状态，再回答用户问题。
8. 若用户的问题涉及已锁定决策（见"技术规格锁定"段），提醒用户该决策已冻结，修改需走变更流程。
```

---

## 项目快照（一句话状态）

**2026-10-14**（W5）：T1.1~T1.5、T2.1~T2.4、T3.1~T3.3 全 DONE。T3.4 设计协商完成并冻结：①协议 v0.9 修订2 加 NODE_ID（12 字节帧、CRC 含地址、LEN≤26），T3.4 跨端改帧；②D1 抽 `enum DataSource` 随 T3.4；③断线不回退模拟、统一三态+看门狗+指数退避重连（T4.1，质量码同步引入）；④就业导向新增 W9~W10 扩展期：Modbus A(T8.5 TCP 服务器)/B(RTU 主站)/C(USART2 RTU 从站)，OPC UA 列项目后目标。下一个动作：开工 T3.4。固件工程已按"复制不删原路径"方式入库 `firmware/stm32_f103_metalmonitor/`（原路径 `D:\code\Firmware_Learn\温度传感器` 保留）。

---

## 关键决策记录（按时间倒序，最新在前）

| 日期 | 决策 | 理由 | 退场/反转条件 |
|---|---|---|---|
| 2026-10-14 | **网络化路线定案（同日二次修订：W5500 入选）**：①T8.1 MQTT 桥留 W8 原位；②**W5500 以太网最小闭环入 W8 T8.6**（SPI2 驱动 + socket TCP 服务器发 AA55 帧 + PC 端 FrameParser 复用，帧格式零改动）；③ESP-01 透传**不做**仅留方向（AT 知识不可迁移、供电坑、演示依赖 WiFi 环境）；④MQTT on MCU / Modbus TCP 从站列**项目后**（W5500 之后的纯增量，排在 OPC UA 前）；⑤PC 上位机"网关"叙事不变，设备侧网络由 T8.6 补齐 | 用户明确求职目的权重最高：面试对话深度取决于**可迁移知识**（socket/TCP 状态机）而非模块说明书（AT）；以太网 JD 关键词命中碾压 WiFi；AA55 帧协议天然适配 TCP 粘包，可讲"同一解析器跑串口+以太网"；F103 引脚已核算（SPI2 PB12~15，避开 PA6 ADC/PA7 DS18B20） | SPI 读版本寄存器（0x0039=0x04）2 天不通→搁置留结论；ioLibrary 移植超 2 天→降级裸 socket echo；挤压 M5（v1.0/README/视频）→**无条件砍 T8.6**；面试季前若确需 WiFi 再议 ESP-01 |
| 2026-10-14 | **完善阶段登记（待排期，不插队）**：系统错误可见化——Core 新增 `IErrorReportService`（分级 / 内存环形列表 / 按 `(source, 异常类型)` 去重聚合 / 事件广播），UI 三出口：徽标（红点数字 = **当前活跃故障源数**）、错误中心页（**仅查看展示**）、弹窗（**同源同类型每会话仅提示一次**）；**内存态不持久化**，重启后新错重新提示；错误范畴仅限**程序级故障**（连接失败、对象为 null 导致读取退化），与设备业务报警**分离**；待改造对象 = 全项目 8 处静默 catch（清单见 error_fix_log #5 第 3 次复核 / 本文件 §P4） | 用户审计发现多处 `try-catch` 静默吞异常（fail-silent），该类问题已累计 **3 次**（#3b 全局处理器吞启动异常 / #5 静默回退模拟数据 / 本次全项目审计）；"一处收集、多处呈现"防「弹窗风暴」 | ①替换 catch 出现功能回归 → 回滚该处，仅留中枢+全局兜底；②错误中心页超预算 → 砍页面与徽标保基础层；③"不持久化"影响排障 → 再引入落盘 |
| 2026-10-14 | **Modbus 三方案 + OPC UA 定位**：A=PC Modbus TCP 服务器（W8 T8.5）；B=PC RTU 主站、C=STM32 USART2 RTU 从站（W9~W10 扩展期，TTL 先行、MAX485 可选）；OPC UA 移出计划为项目后学习目标。AA55 协议在 USART1 冻结不动，两协议双串口并行、共享应用层 | 就业目标为潮汕地区：嵌入式岗多于上位机岗，复合技能（STM32+WPF+Modbus 网关）覆盖控制器厂/集成商/平台型三类雇主；F103 有 3 个 UART，USART2 并行消除"双协议抢一口"的冲突；OPC UA 对 F103 资源不可能（20KB RAM/无以太网），PC 网关形态新手成本 5~7 天 ROI 低 | W6 PID 未收敛→T9.1 只读最小从站；USART2 IDLE+DMA 2 天不通→编译期双 Profile；FluentModbus 半天跑不通→砍除留文档。详见 PROJECT_PLAN W9~W10 退场条件 |
| 2026-10-14 | **断线语义统一**：MQTT/串口断线均"停数据 + 三态提示（Connected/Connecting/Disconnected）+ 应用层看门狗（3 秒无有效帧）+ 指数退避重连"，废止"断线回退模拟数据"；模拟数据仅在用户主动选择时生成；质量码（Good/Stale/Bad+时间戳）随 T4.1 引入，DB 加列推后 | "拿模拟数据顶真数据"=失效未被检出，工业大忌；断线重连串口按 VID/PID 重定位 COM 口，MQTT 重连后必须重新订阅 | T4.1 若 UI 改动挤占 W5 主线，降级为"回退模拟但强制打⚠标"，质量码推 W8 |
| 2026-10-14 | **职责边界**：设备名/位置/阈值/启停用的权威源在上位机（设备管理界面保留，W8 加"数据来源"列）；型号/序列号/指标能力的权威源在设备（设备自描述帧 β），β 仅作演进方向不入计划；当前 6 类指标中仅温度/电压有真实物理来源 | 上下文（怎么被使用）归使用方，身份/能力归设备；β（对标 IO-Link IODD/OPC UA 信息模型）单板 demo 收益≈0，成本 2~3 倍 | 多设备/第二块板出现时重启 β 设计 |
| 2026-10-14 | **协议加 NODE_ID（α）**：AA55 与 LEN 间固定 1 字节，帧 11→12 字节，CRC 覆盖含地址（8 字节），LEN 上限→26；0x00=单节点/未分配；上位机只做"启动只读路由表"（config.json NodeRouting 快照），解析规则永不由配置决定；未知节点计数丢弃；固件当前 `#define DEVICE_NODE_ID 0`；SEQ 留 T5.1 | 多节点扩展前置且字段先冻结代价最低；"机制（解析）与策略（路由）分离"——同字节流两种解析法会错位吞帧；路由表范式同 Modbus 轮询表 | T3.4 跨端改帧超 1 天→固件先发 0x00、线上格式一步到位，多节点 W8 再启用；NODE_ID 永久 0x00 也不算未完成 |
| 2026-09-18 | **AD4184 光耦隔离模块移出关键路径**，降级为 W8 焊接练习耗材；MOSFET 3.3V 通断实测归入 T6.1；焊接不列为 W1-W7 任何任务的验收项 | ①板孔 4mm 与面包板排针 2.54mm 物理不匹配，端子块需焊接 + 螺丝刀；②PTC 仅 0.6W/0.12A，完全不需要光耦隔离；③为 ¥1.38 元件引入焊接门槛，会拖住 M0 却零收益 | 若 W6 前出现必须驱动 >1A 或高压负载的新需求 → 重新评估；（W8 有 3+ 天余力时用 AD4184 练焊接） |
| 2026-09-14 | PCB 打板推迟到 W8 弹性期，W1-W7 用面包板 | 原型阶段功能未定型，打板是返工成本；面试看的是"跑通+讲清"，不是板子漂亮 | W8 有 3+ 天余力才画 PCB；否则用"原型已验证 + PCB 设计文档"交付 |
| 2026-09-13 | 执行器从"风扇"改为"PTC 加热片 + PID 恒温" | 风扇手捏热源不可复现；PTC 阶跃可复现、PID 是温控岗位核心卖点、佛山家电产业带高度匹配 | W6 PID 整定两天仍震荡 → 降级为滞回控制（bang-bang），PID 移入 W8 |
| 2026-09-13 | 通信协议选自定义二进制帧而非 Modbus RTU | 两月窗口内逐字节掌握协议本质 > 调库；已借用 Modbus CRC16 校验；留兼容模式演进路径 | ~~求职时目标公司 JD 明确要求 Modbus → W8 补做 B5~~ **已于 2026-10-14 触发并升级：A/B/C 三方案，见最新决策行（原 B5 废止）** |
| 2026-09-13 | 芯片用现有 STM32F103C8T6 不换 | 零成本、教程密度全国第一、Cortex-M3 是工控 JD 高频词、F103 及国产替代是佛山/潮汕主流 | 板子翻车（假芯片/烧录失败）2 天排除不了 → 换 Nucleo-F103RB 或 GD32 |
| 2026-09-13 | 不用 FreeRTOS，裸机主循环+中断 | 两月窗口塞 RTOS 吃两周，收益不对等；README 注明为演进方向 | — |
| 2026-09-13 | DAP 调试器替代 ST-Link | 已有 DAP，功能等价（烧录+断点+寄存器），零成本 | — |
| 2026-09-13 | 上位机不重构，只新增 SerialService 数据源 | 复用已有架构（DeviceService 已预留"串口"占位），面试叙事="我不是重写，我是扩展" | — |

---

## 硬件清单与状态

### 已有（野火 25 件套）

| 器件 | 型号/规格 | 角色 | 状态 |
|---|---|---|---|
| 主控板 | STM32F103C8T6（LQFP48, 64KB Flash, 20KB RAM） | 控制器 | ✅ 已有 |
| 调试器 | DAP（CMSIS-DAP 兼容） | 开发工具 | ✅ 已有 |
| 温度传感器 | DHT11（精度 ±2℃，整数分辨率） | 不适合 PID，仅备用 | ✅ 已有 |
| 电位器 | 10kΩ | ADC 给定/模拟量输入 | ✅ 已有 |
| 蜂鸣器 | 有源 | 报警输出 | ✅ 已有 |
| OLED | 0.96" I2C | 本地显示（加分项 B3） | ✅ 已有 |
| EC11 编码器 | — | 本地旋钮（加分项） | ✅ 已有 |
| SG90 舵机 | — | 物理指示（加分项） | ✅ 已有 |
| W25Q64 | SPI Flash 8MB | 参数持久化（加分项 B4） | ✅ 已有 |
| USB 转串口线 | — | 备用通信 | ✅ 已有 |
| 面包板+杜邦线 | — | 搭建平台 | ✅ 已有 |

### 已采购（2026-09-13 下单，在途）

| 器件 | 规格 | 价格 | 用途 | 到货状态 |
|---|---|---|---|---|
| DS18B20 模块 | 三针带小板（VCC/DQ/GND，板载 4.7k 上拉） | ¥2.18 | 数字温度反馈（0.1℃ 分辨率） | ✅ 已到货（DALLAS 18B20 批次确认） |
| PTC 加热片 | 5V/70℃/1.2-2W 黄膜款 | ¥2.95 | PID 闭环执行器 | ✅ 已到货（实测冷态 39~41Ω ≈ 0.6W 低功率档，可用） |
| AD4184 MOSFET 模块 | 光耦隔离，3-5V 控制，×2（备件） | ¥1.38×2 | 功率驱动（MCU 引脚→PTC） | ✅ 已到货。**已移出关键路径**：板孔 4mm ≠ 面包板排针 2.54mm，无法免焊接入；降级为 W8 焊接练习耗材。W6 驱动 PTC 走面包板（裸管/临时线） |

**合计约 ¥11（含邮费 ¥4）**

### 不买（明确排除）

| 器件 | 不买理由 |
|---|---|
| ST-Link | DAP 已替代 |
| CH340 模块 | 板载已有 |
| 电机驱动模块 | 套件无，改用 PTC 方案后不需要 |
| 风扇 | PTC 方案下自然冷却足够 |
| 传感器堆料 | 两通道封顶 |

---

## 技术规格锁定（冻结项，修改需走 §C4 变更记录）

### 通信协议 v0.9（W5 T5.4 冻结为 v1.0；细节以 docs/protocol/PROTOCOL.md 为准）

```
当前帧：| AA 55 | NODE_ID | LEN | CMD | PAYLOAD(0..N) | CRC16-L | CRC16-H |
         帧头    节点地址  长度  命令     数据          CRC16/MODBUS(0xA001)
T5.1 后：AA 55 | NODE_ID | LEN | CMD | SEQ | PAYLOAD | CRC16（SEQ 随下行 ACK 加入）
波特率：115200, 8N1（USART1）
字节序：小端（Little-Endian）；帧 12 字节（CMD 0x01）；CRC 覆盖 NODE_ID~状态位共 8 字节；LEN≤26
温度编码：int16，0.1℃ 定点（如 253 = 25.3℃）
电压编码：uint16，mV（如 3300 = 3.3V）
NODE_ID：0x00 单节点/未分配（当前固件固定值）；上位机按启动只读路由表映射，未知节点计数丢弃
```

| CMD | 方向 | 含义 | PAYLOAD |
|---|---|---|---|
| 0x01 | MCU→主机 | 周期数据上报 | 温度 int16 + 电压 uint16 + 状态位 |
| 0x02 | 主机→MCU | 设目标温度+报警上限 | 上限 int16 + 下限 int16 → 回 ACK |
| 0x03 | 主机→MCU | 加热模式 | 0=关 1=手动占空比 2=自动 → 回 ACK |
| 0xA0 | MCU→主机 | ACK | 回显 SEQ |
| 0xA1 | MCU→主机 | NACK | 回显 SEQ + 错误码 |

### 引脚分配（W1 确认板子后最终定稿）

| 功能 | 引脚 | 外设 | 备注 |
|---|---|---|---|
| LED | PA5（或板载） | GPIO 推挽 | 点灯验证 |
| printf / AA55 协议 | PA9(TX)/PA10(RX) | USART1 | 调试输出 + 私有协议（v1.0 冻结，不动） |
| DS18B20 DQ | PA7 | GPIO + us 延时 | 单总线（T2.2 已验证） |
| ADC 电位器 | PA6 | ADC1_IN6 | 模拟量输入（野火 29-2 例程实际用 PA6） |
| Modbus RTU 从站（W9） | PA2(TX)/PA3(RX) | USART2 | 2026-10-14 锁定：TTL 先行，MAX485 可选；与 USART1 双协议并行 |
| W5500 以太网（W8 T8.6） | PB12(CS)/PB13(SCK)/PB14(MISO)/PB15(MOSI) | SPI2 | 2026-10-14 锁定：SPI1 默认引脚 PA5~7 与 PA6 ADC/PA7 DS18B20 冲突故走 SPI2；硬件协议栈在 W5500 片内，F103 侧仅 socket API（ioLibrary） |
| MOSFET PWM | 待定 | TIMx_CHx | 500Hz（光耦限制） |
| 蜂鸣器 | 待定 | GPIO | 有源，高电平响 |

### PID 参数（W6 整定后填入）

| 参数 | 值 | 整定方法 |
|---|---|---|
| Kp | 待定 | — |
| Ki | 待定 | — |
| Kd | 待定 | — |
| 阶跃响应稳态误差 | ≤±0.5℃ | 40→50℃ 阶跃 |

---

## 文件地图

| 文件/目录 | 用途 | AI 读取优先级 |
|---|---|---|
| `PROJECT_CONTEXT.md`（本文件） | AI 会话引导扇区 | **必读** |
| `PROJECT_PLAN.md` | 8 周计划与任务验收表 | **必读** §0 + 当前周 |
| `error_fix_log.md` | 错误修复日志与经验教训 | 必读底部教训表 |
| `MetalMonitor.sln` | VS 解决方案 | 构建入口 |
| `MetalMonitor.core/Services/` | 核心服务层（DeviceService/MqttService/DatabaseService/AlarmService） | 修改串口时读 |
| `MetalMonitor.core/Services/DeviceService.cs` | 设备管理+数据管线，已预留"串口"占位 | 新增 SerialService 时读 |
| `MetalMonitor.core/Services/IMqttService.cs` | MQTT 服务接口（SerialService 仿此模式） | 仿写时读 |
| `MetalMonitor.UI/ViewModels/MainViewModel.cs` | 主界面 ViewModel，定时器+数据刷新 | 接入串口数据时读 |
| `MetalMonitor.UI/App.xaml.cs` | DI 容器注册+全局异常处理 | 注册新服务时读 |
| `MetalMonitor.Tests/` | 单元测试项目（含 InternalsVisibleTo） | 写 FrameParser 测试时读 |
| `config.json` | 运行时配置（不在 C 盘，.sln 同级） | — |
| `firmware/stm32_f103_metalmonitor/` | Keil 固件工程（自包含、.uvprojx 内无绝对路径）；2026-10-14 自 `D:\code\Firmware_Learn\温度传感器` 整目录**复制**入库，原路径保留未删 | T3.4 改 NODE_ID 先读 `App/temperature/app_temperature.c`（L55 起组帧）+ `User/crc16/bsp_crc16.c` |
| `docs/protocol/PROTOCOL.md` | 协议文档（W3 起草，W5 冻结） | W3 创建 |
| `tools/` | PC 端自测脚本（CRC16 对照等） | W2 T2.3 创建 |
| `assets/` | 接线照片/截图/GIF（已加 .gitignore 例外） | 持续积累 |
| `docs/test-reports/` | 压测报告 | W6 创建 |

---

## 当前进度指针

| 项 | 值 |
|---|---|
| 当前周次 | W5（10-11~10-17）M3：下行控制 + MCU 本地自治；T4.1~T4.3（W4 遗留）仍待补 |
| 当前里程碑 | M0 已达成；M1（T2.2~T2.4）已达成；**M2-a（T3.1~T3.4）已达成（2026-10-19 真机实测收口）**；追赶 M2-b（T4.1~T4.3，W4 遗留） |
| WIP 任务 | 无；T3.4 已 DONE（2026-10-19：代码五步 80/80 ✅ + 真机烧录实测三验收标准全达成 ✅） |
| 已完成 | T1.1~T1.5 全 DONE；T2.1 DONE；T2.2 DONE（2026-10-02）；T2.3+T2.4 DONE（2026-10-06）；T3.1 DONE（2026-10-06）；T3.2 DONE（2026-10-10 ISerialService + SerialService 115200 8N1）；T3.3 DONE（2026-10-12 FrameParser 环形缓冲区+枚举状态机；2026-10-14 LEN 上下界校验修复 + 10 单测 58/58 通过）；**T3.4 DONE（2026-10-19 DeviceService 串口接线 + NODE_ID 12 字节帧跨端落地 + D1/D3 技术债关闭，dotnet test 80/80；真机实测：真实帧进卡片/曲线、0x2A 未知节点计数丢弃不崩溃、改回 0x00 自动恢复）** |
| 下一个动作 | 补 T4.1~T4.3（W4 遗留）：串口断线三态 + 应用层看门狗（3 秒无有效帧判通讯中断）+ 指数退避重连（按 VID/PID 重定位 COM 口）+ 连接三态 UI；**断线不回退模拟数据**；质量码枚举随 T4.1 引入 |
| 技术债状态 | P1 表：**D1 ✅**（数据源硬编码 → `enum DataSource`）与 **D3 ✅**（热路径 O(n) 按名查找 → 按名索引）均已随 T3.4 关闭；D2（ConfigService 双副本）与 D4（断线只翻旗标）未动，D4 按计划归 T4.1 |
| 硬件依赖 | 无（板载 USB 转串口已验证，DS18B20 已接 PA7）；W9 需第二根 USB-TTL 线（套件已有备用），MAX485 为可选采购；**W8 T8.6 需 W5500 模块（~25 元，SPI2 接线 + 网线，直连 PC 静态 IP 即可演示）待采购** |
| 阻塞项 | 无 |
| 追赶排期剩余 | W4 闸门（10-10）评审：M2 未达成（仅完成至 T3.1，T3.2~T4.3 滞后）。用户决策 **不降级硬赶**，续做 T3.2~T4.3；退场条件：若 T3.2 或 T3.3 实际耗时超估算 2 倍则立即停下重估降级 —— 两者均已按期完成，未触发。下一步补 T4.1~T4.3（T3.4 已于 2026-10-19 真机实测收口） |
| 降级条件 | A6 降级（保下行 ACK、报警降 LED）。**当前状态：未触发**。另：原"延至 10 周"预案已于 2026-10-14 正式落地为 **W9~W10 扩展期（Modbus 双端）**，启用前置为 M5 达成，与 A6 降级互不影响 |

---

## 开放问题（待解决/待确认）

| 问题 | 状态 | 关联任务 |
|---|---|---|
| 板子是否板载 USB 转串口？ | ✅ 已确认板载（T1.5 串口输出正常） | T1.5（已解决） |
| DS18B20 到货后需实测 3.3V 通断 | ✅ 已到货且 T2.2 已验证（自写单总线驱动，DQ=PA7，室温~手捏温升正常） | T2.2（已解决） |
| MOSFET 3.3V 通断实测 | 已归入 T6.1（W6），W1 不阻塞；AD4184 免焊接入失败，W6 改用面包板方案 | T6.1 |
| 保温腔用什么材料（纸盒/泡沫箱） | 待 W6 前准备 | T6.1 |

---

## 技术债务与优化待办（已识别 · 暂不修复）

> 2026-10-10 全代码梳理后登记。**只记录，不插队**——处理时机见各行"建议时机"，不得影响 PROJECT_PLAN 既定任务。
> 行号以登记日版本为准，代码迭代后以"文件+方法名"定位。

### P1 · T3/T4 串口与断线任务落地时应一并处理

| # | 位置（文件 · 方法） | 问题 | 建议改法 | 触发/退场条件 |
|---|---|---|---|---|
| ✅ D1 | DeviceService · `SetDataSourceAsync` | 数据源用硬编码中文字符串 `"模拟数据"/"MQTT"/"串口"` + switch 分支，加第 4 种源要改 4 处（数组/switch/UI/配置） | T3.4 串口落地时抽 `enum DataSource` 或 `IDataSource` + 工厂，顺手统一 | 新增第二种真实数据源（串口）时即动手 |
| D2 | DeviceService / DatabaseService 构造函数 | 各自 `new ConfigService()` 持独立内存副本，setter 写盘但跨实例不互见，需手动 `Reload()`。**当前靠"两副本读写字段不重叠"侥幸安全**（DeviceService 用 BatchSize/DataSource，DatabaseService 用 DatabasePath） | 收归 DI 单例注入 `IConfigService`，禁止业务服务自行 new | 出现共享字段（如 RefreshInterval 被第三方读取）或第三个持有者时必须改 |
| ✅ D3 | DeviceService · `GetMetricUnit` / `UpdateDeviceFromMqtt` | MQTT 高频回调路径上 `FirstOrDefault` 按名 O(n) 找配置、找设备 | ReloadMetricConfigs 时同步构建 `Dictionary<string,MetricConfig>`、`Dictionary<string,Device> _devicesByName` | 指标/设备数量增长或消息率升高前改掉；改动小，可随 D1 一起做 |
| D4 | MqttService · `OnDisconnected` / DeviceService · `OnMqttDisconnected` | 断线只翻 `_useMqtt=false` 旗标回退模拟数据，**无自动重连** | 已在 T4.1 规划（5s 自动重连+UI 提示），此处仅占位提醒，不另立任务 | T4.1 处理 |

### P2 · 质量与排障（找空档做，不阻塞里程碑）

| # | 位置（文件 · 方法） | 问题 | 建议改法 |
|---|---|---|---|
| D5 | DeviceService · `OnMqttMessageReceived` | `catch {}` 静默吞掉所有解析异常，坏消息无日志无计数；T3 串口坏帧接入后排障会更难 | 至少走 RollingFileLogger 记一条 + 维护坏帧计数（可暴露给 UI 状态栏） |
| D6 | MainViewModel · `BrowseForFolder` 及 IFileOpenDialog/IShellItem COM 声明（约 L351-691） | 文件夹选择 P/Invoke 占 VM 一半篇幅，职责越界；且 COM interop 不可跨平台 | 抽 `Services/IFolderBrowserService`（Windows 实现放 UI 层），VM 只依赖接口 |
| D7 | DeviceService · `_lastSimValues` | 模拟值上一值缓存只增不减，设备/指标删除后键残留，长跑内存泄漏 | SyncDevices 增量同步后顺带清理不存在的 deviceId 键；或设备删除时主动清 |
| D8 | DeviceService · `OnMqttMessageReceived` | `JsonSerializer.Deserialize<MqttDeviceData>(e.Payload)` 用默认选项，每条消息重复构建序列化元数据，**违反项目"JsonSerializerOptions 缓存为 static readonly"约定** | 提 `private static readonly JsonSerializerOptions s_jsonOptions`（或直接用无选项重载+源生成器），与 ConfigService 对齐 |
| D9 | AlarmService · `CheckAndNotify` | 内部又调一次 `ShouldAlarm`，与调用方刚做的判定重复查一次字典锁 | 调用方把已算好的 `currentAlarm` 结果传入（改签名需同步两条数据通路） |

### P3 · 规模化才需处理（当前规模刻意不做，避免过度设计）

| # | 问题 | 现状为何不痛 | 动手信号 |
|---|---|---|---|
| D10 | DatabaseService · `AddDataRecordsBatchAsync` 内 foreach 逐条 ExecuteNonQuery | 每批 50 条，单事务已摊薄提交开销 | 批量 ≥ 500 条/批或写吞吐成为瓶颈时，改多值 `VALUES (...),(...)` 单语句 |
| D11 | MainViewModel · `RefreshAvailableMetrics` 双层 Any 为 O(n²) | 指标固定 6 个 | 指标数 ≥ 50 时改 HashSet 比对 |
| D12 | DeviceCardViewModel · `AddMetricPoint` 滚动剔除后全量重排 X 为 O(n) | MaxHistoryPoints=60 | 点数上限调到 ≥ 500 时改环形缓冲 |
| D13 | 报警仅 Above/Below 两态，无窗口/速率/迟滞报警 | 当前阈值报警满足演示 | 真实工况需要"连续 N 拍越限""变化率超限"时，抽 `IAlarmPolicy` |
| D14 | 历史查询无缓存；DeviceType 自由文本无法按类型聚合 | 单机数据量小、查询频率低 | 历史查询频繁重复 / 需按设备类型统计报表时再做 |
| D15 | 单门面 DeviceService 成热点，三源共用一个 _pendingBatch | 当前消息率低 | 消息率持续 ≥ 5000 点/秒或设备 ≥ 100 台时，拆每数据源独立 Ingestion + 独立写通道（架构级改动，非小修） |

### P4 · 完善阶段：系统错误可见化（2026-10-14 登记，**待排期，只记录不插队**）

> 触发：用户审计发现全项目大量 `try-catch` **静默吞异常**（fail-silent）。该类问题在 error_fix_log 累计已达 **3 次**（#3b 全局处理器吞启动异常 / #5 MQTT 静默回退模拟数据 / 本次全项目审计），达到需专项处理的阈值。
> **定位**：抵达 PROJECT_PLAN「完善阶段」（待排期段）后再展开任务明细；现阶段**不占任何周次、不插队**既定任务。

**四项设计决议（用户 2026-10-14 拍板）**

| # | 决议 | 内容 |
|---|---|---|
| 1 | 阶段定位 | 仅登记，最后抵达完善阶段再继续完善计划；记录对象 = 项目代码里 try-catch 中**本应有错误提示**的地方 |
| 2 | 徽标数字 | 红点数字 = **当前活跃故障源数**（按来源去重，非原始抛出次数、非未读数） |
| 3 | 弹窗策略 | 原本置空的 try-catch 报错时弹窗；**同源同类型每会话仅提示一次**；**内存态不持久化**，重启后新错重新提示；错误中心页**仅做查看与展示** |
| 4 | 错误范畴 | 仅**程序级故障**（连接失败、对象为 null 导致读取退化等），类比 IDE 对代码逻辑的报错；**不混入**设备传上来的业务报警/错误数据 |

**设计骨架（一处收集、多处呈现）**

- Core：`IErrorReportService` —— 分级（Fatal/Error/Warning/Info）、内存环形列表、按 `(source, 异常类型)` 去重聚合（累计 Count）、变更事件广播；系统故障与业务报警**分离**（业务报警仍走 AlarmService / AlarmLogs）。
- UI 三出口：①**徽标**（入口红点 + 活跃故障源数）；②**错误中心页**（查看 / 展示）；③**弹窗**（同源同类型每会话一次，防「弹窗风暴」）。
- 分层约束：UI 只消费事件，Core **不得反向依赖 UI**。

**待改造对象：现有 8 处静默 catch**（行号以 2026-10-14 工作区版本为准，HEAD=`bd2153`；完整表见 [error_fix_log.md](file:///d:/code/MetalMonitor/error_fix_log.md) #5 第 3 次关联复核）

| # | 位置（文件 · 方法） | catch 内行为 | 建议定级 |
|---|---|---|---|
| 1 | DeviceService · `SetDataSourceAsync` | `FlushAsync` 吞掉；MQTT 失败静默回退模拟数据 | Error |
| 2 | DeviceService · `SetDataSourceAsync` 断开分支 | `DisconnectAsync` 吞掉 | Warning |
| 3 | DeviceService · `ReloadMetricConfigsAsync` | `FlushAsync` 吞掉 | Warning |
| 4 | DeviceService · `ReloadDevicesAsync` | 冲刷缓冲异常吞掉 | Warning |
| 5 | SerialService · 串口接收异常 | 仅 `Console.WriteLine` | Error |
| 6 | RollingFileLogger · 日志写入失败 | 静默（**故意静默，不得改**，否则"写日志失败→报错→再写日志→又失败"递归） | 保持 |
| 7 | MainViewModel · 每秒刷新回调 | 异常混入 `AlarmLogs` | 分流到错误中心 |
| 8 | App.xaml.cs · 全局兜底 | `MessageBox` + 落盘（需接入中枢） | Fatal |

**退场/反转条件**

1. 替换 catch 过程中出现任何原功能回归 → 立即回滚该处，只保留新增中枢与全局兜底；
2. 错误中心页 UI 超预算 → 砍页面与徽标，保留基础层（全链路错误可见才是真价值）；
3. "内存态不持久化"若被证明影响排障（重启后丢失现场）→ 再引入落盘。

### 处理原则

1. P1 中 D1/D3 随 T3.4 同一批改（趁手重构，不单独提交）；D4 走 T4.1。
2. P2 每条独立小提交，提交信息注明 D#，便于回溯。
3. P3 **不到动手信号不处理**——当前规模强行优化属于过度设计。
4. 修复后将对应行状态改为 ✅ 并注明日期/commit，不删除历史行。
5. P4 **仅登记、不插队**——抵达 PROJECT_PLAN「完善阶段」后再展开任务明细；处理时机未到前，不得改动 8 处 catch 的行为。

---

## 更新协议（AI 每次会话结束时执行）

```
1. 更新"项目快照"段为当前状态一句话。
2. 若有新决策，追加到"关键决策记录"表（最新在前）。
3. 若硬件状态变化（到货/实测结果），更新"硬件清单"对应行的"状态"列。
4. 若技术规格变化（引脚分配/PID 参数），更新"技术规格锁定"对应段。
5. 更新"当前进度指针"段的 WIP 任务和下一个动作。
6. 若有新问题或问题解决，更新"开放问题"段。
7. 在本段下方追加一行更新日志。
```

### 更新日志

| 日期 | 更新人 | 变更摘要 |
|---|---|---|
| 2026-09-14 | AI（会话持久化初始化） | 创建文件，导入全部历史决策、硬件清单、技术规格、文件地图 |
| 2026-09-14 | AI | T1.5 DONE（串口 printf+断点单步通过）；确认板载 USB 转串口；开放问题更新 |
| 2026-09-14 | AI | T1.2 DONE（commit 103bf9d，git status 干净）；W1 实质收官 |
| 2026-09-16 | AI | 三件硬件到货状态更新；PTC 定论 0.6W 低功率档（error_fix_log #22 收尾）；进度指针指向 T2.1 |
| 2026-09-17 | AI | T2.1 DONE（野火 29-2 ADC 例程跑通）；ADC 引脚由 PA0 修正为 PA6；进度指针指向 T2.3/T2.2 |
| 2026-10-06 | AI | T2.3+T2.4 DONE：CRC16 两端对同一载荷一致（0x070C）；11字节二进制帧1Hz上报，温度/mV/状态位手工解析正确；进度追平 W2 末，下一个动作 T3.1 PROTOCOL.md |
| 2026-09-18 | AI | T1.1 DONE（四张订单/到货照片齐备，验收列满足）；AD4184 移出关键路径降级为 W8 焊接练习耗材；MOSFET 实测归入 T6.1；W1 收官、M0 达成，进度指针指向 T2.3；同步登记 error_fix_log #23 |
| 2026-09-30 | AI | 基线评估：落后 12 天（W2~W3前半无产出），暂不延至 10 周；国庆 8 天追赶窗口（10/1~10/10 每天 C 档）；T2.5 砍并入 T4.3、T3.1 压缩至 1h、T3.3 单测砍至 2 用例；C1 补两条缺数据日志；进度指针更新为追赶排期 + 降级条件 |
| 2026-10-10 | AI | 全代码梳理：新增「技术债务与优化待办」段，登记 D1~D15（P1 随 T3.4/T4.1 处理、P2 空档做、P3 到信号才动）；架构分析仅入此文件，不改动任何代码 |
| 2026-10-10 | AI | T3.1 状态回写 DONE：PROTOCOL.md v0.9 早于 10-06 成稿，今日对照验收标准（帧格式/CMD表/定点数/字节序/帧示例hex）逐条核对通过；进度指针由过期状态更新为「W4 收尾日，实际仅完成至 T3.1，T3.2~T4.3 滞后，需评估是否触发 A6 降级」 |
| 2026-10-10 | AI | T3.2 DONE：新增 ISerialService.cs + SerialService.cs（MetalMonitor.core/Services/）；接口含 DataReceived + ConnectionStateChanged 事件；SerialPort 按 PROTOCOL.md v0.9 配置 115200 8N1，仅收原始字节不做帧解析；dotnet build 0 错误通过；进度指针指向 T3.3 FrameParser |
| 2026-10-12 | AI | T3.3 DONE：新增 FrameParser.cs（128 字节环形缓冲区+枚举状态机+坏帧跳过字节重找帧头）+ SensorDataFrame；6 个单测（正常/半帧/粘包/坏CRC/帧头错位/坏帧恢复）dotnet test 54/54 通过；用户全程参与设计决策（4 个 AskUserQuestion）；进度指针指向 T3.4 DeviceService 接入串口 |
| 2026-10-14 | AI | T3.3 收口：修复 LEN 字段双侧校验漏洞（此前仅校验上限，LEN=0 会让状态机在 ReadingBody 滞留），新增 MinLen=1 / MaxLen=32-5=27 常量，非法 LEN 时 BadFrameCount++ 且回退 `_totalRead` 重扫（该字节可能恰为真帧头 0xAA）；补 4 个边界单测，dotnet test 58/58 通过；PROTOCOL.md §2 补录"单帧 ≤32 字节"跨端约束；error_fix_log #31/#32 已记录；进度指针指向 T3.4 |
| 2026-10-14 | AI | 固件工程入库：按用户决策以**复制**方式（原路径不删）将 `D:\code\Firmware_Learn\温度传感器` 整目录搬入 `firmware/stm32_f103_metalmonitor/`（249 文件 / 32MB）；核查 `.uvprojx` 内无绝对路径（自包含可整目录搬迁）、`.gitignore` Keil 段生效（`**/Objects/`、`**/Listings/` 已忽略，`.uvprojx` 保留入库，firmware 下待入库 109 文件）；删除已无用的占位 `.gitkeep`；"文件地图"该行由"待建"更正为实际路径与改帧入口文件 |
| 2026-10-14 | AI | T3.4 第①步（固件组帧）完成：`app_temperature.h` 新增 `#define DEVICE_NODE_ID 0x00`（含 §2.1 语义注释）；`app_temperature.c` L48 `frame[11]`→`[12]`、L55-56 插入 `frame[2]=DEVICE_NODE_ID` 并将原 LEN/CMD/载荷整体后移一位、L62 `CRC16_Modbus(&frame[2], 7→8)`（**用户原注释措辞保留，仅把 7 改 8 并补 NODE_ID(1)**）、发送循环 `i<11`→`i<12`；用 Keil UV4 命令行（`D:\mdk\Keil_v5\UV4\UV4.exe -b`）**从新路径**构建 0 Error 0 Warning（Code=8012B），一次同时验证"整目录搬迁可编译"与"改帧可编译"；PC 对照脚本 `tools/Crc16ModbusTest` 新增向量 `00 06 01 FD 00 E2 0C 01` → **0xF82E** PASS，与 PROTOCOL §7 示例帧 `AA 55 00 06 01 FD 00 E2 0C 01 2E F8` 一致；T3.4 状态置 DOING，待用户烧录后以串口助手实测 12 字节新帧收口 |
| 2026-10-14 | AI | T3.4 第②步（FrameParser 12 字节改造）完成：用户先行手改（新增 ReadingNodeId 状态、MaxLen=26、SensorDataFrame.NodeId、偏移后移、部分注释），AI 复查发现 3 处缺陷并修复——WaitingHead2 未转向 ReadingNodeId（真帧全部死于 NODE_ID=0x00 被 LEN 校验拒绝）、LEN 误写 `_frameBuffer[2]`、NODE_ID 误读 `[3]`，详见 error_fix_log #33；10 个旧用例向量重算（基准帧 AA 55 00 06 01 FD 00 E2 0C 01 2E F8，CRC 覆盖 8 字节）+ 新增 NODE_ID=0x2A 提取用例（CRC 0x9FAC，验证机制/策略分离——解析器只提取不判权责）；dotnet test 59/59；下一步③D1 枚举 / ④配置路由表 / ⑤DeviceService 接线，烧录攒到收口 |
| 2026-10-14 | AI | T3.4 第③步（D1 抽枚举）完成：新增 `MetalMonitor.core/DataSource.cs`——`enum DataSource { Simulation, Mqtt, Serial }` + `DataSourceExtensions`（ToDisplay/TryToDataSource 双向映射，"模拟数据"/"MQTT"/"串口"三种字符串全项目只在此表定义）；DeviceService 内部全枚举化（字段/switch/三处回退点），`DataSource` 属性与 `AvailableDataSources`（改由枚举自动生成）保持 string 对外，IDeviceService/MainViewModel/ConfigService/config.json 零改动；遇到"枚举类型名被同名 string 属性遮蔽"编译错（CS1061），以 `using DataSourceKind` 别名化解并记 error_fix_log #34；行为改进 1 处：SetDataSourceAsync 收到未知名称由"原样持久化垃圾值"改为"统一回退模拟数据"；新增 DataSourceTests 9 用例锁定显示名精确值与双向映射，dotnet test 68/68 |
| 2026-10-14 | AI | 网络化路线定案（用户决策）：T8.1 留 W8；ESP-01 透传不做仅留方向；W5500 列项目后第一候选；已写入决策表 |
| 2026-10-14 | AI | **网络化路线二次修订（用户决策：求职权重上调，W5500 提前入选）**：W5500 以"最小闭环"入 W8 新增 **T8.6**（SPI2 PB12~15 + ioLibrary + socket TCP 服务器发 AA55 帧，PC 端 TCP 适配器喂现有 FrameParser，帧格式零改动）；ESP-01 透传维持不做；MQTT on MCU / Modbus TCP 从站列项目后；退场条件三条（SPI 自检 2 天不通搁置 / ioLibrary 超 2 天降级 echo / 挤压 M5 无条件砍）落决策表；引脚表补 SPI2 行；硬件依赖加 W5500 模块（~25 元）待采购；本咨询轮次零代码改动，T3.4 主线不受影响（⑤仍为下一个动作） |
| 2026-10-14 | AI | T3.4 第④步（配置通道）完成：config.json 新增 `Serial` 段——`PortName`（端口持久化，连接成功后由第⑤步写穿落盘）+ `NodeRouting`（NODE_ID 十六进制键→设备名，**程序内无写入通道**仅手工编辑，兑现"启动只读快照"决策）；ConfigService 加 `SerialPortName` 属性与 `GetNodeRoutingSnapshot()`（返回 `IReadOnlyDictionary<byte,string>` 独立副本，非法键如 "GG"/"0x0F" 静默忽略不挡启动，0x00 不入表由消费方按"缺省取第一台"处理）；旧版无 Serial 段配置向后兼容；新增 4 个单测（端口持久化回环/旧配置兼容/hex 键解析与坏键忽略/快照副本只读性），dotnet test 72/72；测试中一处误断言（"2A"与"2a"为同一字节键，Count 应为 1 非 2）当轮修正并记 error_fix_log #35 |
| 2026-10-14 | AI | T3.4 开工前设计协商（用户主导决策，AI 出选项与算账）：①NODE_ID 进帧（α），PROTOCOL v0.9 修订2（12 字节、CRC 含地址 8 字节、LEN≤26、示例帧 AA 55 00 06 01 FD 00 E2 0C 01 2E F8/CRC 0xF82E）；②路由表仅决定路由、解析格式恒定（机制/策略分离），config.json 启动只读快照；③职责边界锁定（名称/阈值归上位机，能力归设备，自描述 β 列演进）；④断线统一停数据不回退模拟，三态+看门狗+指数退避，质量码随 T4.1；⑤D1 抽 `enum DataSource` 随 T3.4；⑥就业导向（潮汕嵌入式岗更多）新增 W9~W10 Modbus 扩展期：T8.5 PC TCP 服务器、T9.1 STM32 USART2 RTU 从站（IDLE+DMA）、T9.2 PC RTU 主站、T9.3 可选 MAX485；⑦OPC UA 移出计划列项目后目标。三份文档已回写（PROTOCOL.md / PROJECT_PLAN.md 含 §C4 两条变更 / 本文件含 4 条决策+引脚+指针）；下一步开工 T3.4 |
| 2026-10-14 | AI | 完善阶段登记（待排期，不插队）：用户审计发现全项目多数 `try-catch` 静默吞异常（fail-silent），该类问题累计已达 **3 次**（#3b 全局处理器吞启动异常 / #5 MQTT 静默回退模拟 / 本次全项目审计）。登记四项设计决议（①只记录、不排期 ②红点数字 = **当前活跃故障源数** ③弹窗**同源同类型每会话一次**且**内存态不持久化** ④仅记**程序级故障**、与业务报警**分离**）+ 设计骨架（`IErrorReportService` 一处收集、UI 三出口：徽标/错误中心页/弹窗）+ 8 处静默 catch 待改造清单（含 1 处"故意静默不得改"分界）+ 三条退场条件。同步写入 error_fix_log #5 第 3 次关联复核、PROJECT_PLAN「完善阶段（待排期）」段 + §C4 变更行；本文件新增决策表行、§P4 段、处理原则第 5 条 |
| 2026-10-19 | AI | T3.4 第⑤步（DeviceService 串口接线 + 数据管线重构）完成：按用户决策落地"**接入事件驱动 + 派发节拍驱动**"混合模型——接入线程（串口/MQTT）解析/生成后只写线程安全暂存区（同键 latest-wins）并追加落库缓冲（**双轨**：UI 只派发最新值、库保留全量样本），心跳 `ApplyPendingSamples` 排空暂存区执行**全项目唯一一套**"报警判定→SetMetricAlarmState→SetMetricValue→CheckAndNotify（按设备+指标边沿触发）"，MQTT 通路对齐重构（原直接三步 → 写暂存区+落库），改造后所有设备写值与 UI 事件收敛到心跳单线程；新增 `MetalMonitor.core/FrameRouter.cs`（`RouteOutcome` 四态：RoutedByTable/DefaultFirstDevice/UnknownNode/UnroutedDevice）兑现"机制（解析）与策略（路由）分离"，新增 `UnknownNodeCount`/`UnroutedDeviceCount` 两类未命中计数（丢弃帧 + 日志，不崩溃）；DI 注册 `ISerialService`+`FrameParser`，DeviceService 增设 5 参构造函数（DI 自动选中）；`SetDataSourceAsync` 串口分支改为真实连接（端口优先用持久化值、为空回退首可用口）并回写 `SerialPortName`，失败回退模拟；**断线检测/看门狗/三态 UI 未动，仍留 T4.1**；新增 FrameRouterTests 7 例，既有 MQTT 缓冲用例按新语义补一拍心跳（见 error_fix_log #36）；AI 独立复跑 `dotnet test` 79/79、`dotnet build` 0 错误；同步修正 4 处过期注释文案（class 级通路说明、`AvailableDataSources`、`SetDataSourceAsync` remarks）；D1 技术债关闭（表内标 ✅），D3 待用户确认是否本批一并处理 |
| 2026-10-19 | AI | T3.4 收口批：D3 技术债（热路径按名 O(n) 查找）按用户决策随 T3.4 一并处理——`ReloadMetricConfigsAsync` 复用的那份 `Dictionary<string,MetricConfig>` 直接存为 `_metricConfigsByName` 供 `GetMetricUnit`（并保留旧 `?? string.Empty` 语义），新增 `_devicesByName` 按名索引供 `UpdateDeviceFromMqtt`（`TryGetValue` 取代 `FirstOrDefault`）；索引**不做增量维护**——设备集合增删与**改名**（`existing.Name = dbDevice.Name`）全部发生在 `SyncDevicesFromDatabaseAsync` 的 `_devicesLock` 临界区内，故只在该点调用 `RebuildDeviceIndex()` 整体重建即与 `_devices` 强一致（同名取先出现者，与旧语义逐字对齐）；新增回归用例 `DeviceRenamed_AfterReload_StillRoutedByNewName` 并以"临时摘掉重建→该用例必红→恢复"验证其判别力（非空过），`dotnet test` 80/80、`dotnet build` 0 错 0 告警；P1 表 D1/D3 均已标 ✅，剩 D2、D4（D4 归 T4.1） |
| 2026-10-19 | 用户+AI | **T3.4 真机烧录实测通过，置 DONE，M2-a 达成**：①用户烧录固件后串口助手 HEX 模式抓到 1Hz 12 字节帧（`AA 55 00 06 01 32 01 E2 0C 01 7B 14` 等；首行为固件 printf 启动文本 `----ds18b20传感器实验开始----`，属噪声，旁证解析器跳噪声锁帧头设计），Exec 验算 7 组 CRC16-Modbus 全 PASS（"123456789"→0x4B37 算法锚点 + 黄金向量 0xF82E + 5 组实机载荷），确认两端 CRC 同源；②上位机"串口"数据源（config.json 持久化 COM6）下首卡"一号冲压机"温度 30.6℃ / 电压 ~3.30V 每秒真实跳动；电压 ±2mV 抖动为 12 位 ADC 正常量化噪声（1 LSB≈0.8mV）；用户手捂 DS18B20 温度单调爬升呈热惯性曲线，排除"噪音干扰"疑虑（噪音过不了 CRC 且不会响应物理刺激）；③用户将 `DEVICE_NODE_ID` 改 `0x2A` 重烧（全程未关上位机）：卡片停更、程序不崩溃（帧按 UnknownNode 计数丢弃），改回 `0x00` 重烧后数据自动恢复——三条验收标准（真实帧进卡片/曲线、数据源显示"串口"、未知 NODE_ID 计数丢弃不崩溃）全部达成；PROJECT_PLAN 与本文件状态行已回写；下一个动作：T4.1~T4.3（断线三态+看门狗+指数退避重连） |
