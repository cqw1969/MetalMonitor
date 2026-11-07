# GLOSSARY

- **DI（依赖注入）**：对象不自己 new 依赖，由容器按注册表构造并注入。本项目容器在 App.xaml.cs 的 services 注册表。
- **IsAvailable / IsConnected**：能力维度（硬件/服务此刻存不存在）/ 状态维度（当前连没连上）。串口场景：IsAvailable=系统存在串口硬件，IsConnected=端口已打开。
- **InvalidOperationException**："当前状态下此操作无意义"的专用异常。本项目用于两处：DI 注册缺失时容器抛出；能力不可用时服务方法主动抛出。
- **构造时快照 vs 动态计算**：取值一次性定型 / 每次访问重新求值。快照省开销但会过期，动态计算始终新鲜但有查询成本。
- **数据源三态（DataSourceState）**：Online=正常；Faulted=能力在但本次连接失败（可重试）；Unavailable=系统根本没这能力（重试也无效）。与"用户选了什么"正交，故障只改状态不改选择。
- **sealed（封闭类）**：编译期修饰符，禁止该类被继承。比方：房产证上的"禁止转租"条款。本项目约定见 ARCHITECTURE.md（D-7）；注意 sealed 不影响 DI，也不影响接口 Mock（仅影响类 Mock/EF 代理等运行时子类化机制）。
- **依赖方向（Dependency Rule）**：分层架构的硬判据——内层（Core/领域）的代码不得引用外层（UI/框架）的类型，外层可以依赖内层。判断"某类型能否放 Core"的第一问永远是"它引用了谁"，而不是"它看起来是不是基础设施"。
- **SignalR**：ASP.NET Core 的实时推送库，让服务器能主动向浏览器推消息（替代浏览器定时轮询）。本项目若做网页版，报警与遥测将由它从 Core 推到浏览器。
- **程序集/类库（DLL）**：编译产物，不是能独立运行的程序，而是等待被宿主加载的"零件盒"。本项目 MetalMonitor.core 编译为 MetalMonitor.core.dll。
- **宿主（Host）**：真正启动的进程，负责加载类库并让它跑起来。本项目当前宿主是 MetalMonitor.UI.exe；未来网页版的宿主是 ASP.NET Core 服务器进程。浏览器永远不直接加载类库——它只跟服务器说话。
- **Infrastructure（基础设施层）**：Clean Architecture 中的一层，放与领域无关的支撑件（文件、网络、日志）。本项目暂未单独立层，以 Core 内子文件夹承载（如 Collections/）。
- **ADR（架构决策记录）**：Architecture Decision Record，一份轻量文档，记录"为什么这么选"——背景、选项、决定、理由、代价、退场条件。本项目的 ADR 就是 .agent/DECISIONS.md 里的 D-1~D-7。
- **测试网（测试安全网）**：行业比喻，非正式术语——一套自动化测试集合，在你改代码时兜住行为不回归：改坏了测试变红，立刻知道。本项目的测试网 = MetalMonitor.Tests 的 109 个用例。
- **线程池（ThreadPool）**：CLR 维护的共享工人线程队列——有任务借一个跑，跑完归还，不为每个任务常驻线程。本项目心跳（System.Timers.Timer.Elapsed）与 await 延续都跑在池线程上，每秒借到的可能不是同一个物理线程。
- **封送（Marshal）/ Dispatcher**：把一次调用从后台线程"快递"到 UI 线程的消息队列里执行。这是跨线程回 UI 的唯一合法方式；卡片值刷新见 DeviceCardViewModel.cs L305-307，卡片集合增删见 MainViewModel.cs L172。
- **单向数据流**：数据只朝一个方向流（接入→模型→视图），视图只呈现、不改数据。本项目经 MetricValueChanged 事件 + OneWay 绑定实现；UI 侧唯一的"加工"是格式化（ValueText），属呈现适配而非改数据。
- **软实时 / 硬实时**：实时 = 在死线之前完成，衡量的是"可预期的截止期限"而非速度。硬实时误死线即事故（PLC 扫描周期、下位机采样节拍）；软实时误死线只降体验（HMI 监控）。上位机属软实时，业界惯例画面刷新 250ms~2s，且以质量戳标注新鲜度代替盲目追低延迟。
- **链路健康（LinkHealth）**：对"这条通道最近还在不在正常送数据"的判定。比方：快递站告诉你"最近还有没有车来"，对应"线断/对端断电"；"车来了但箱子解析不了"对应乱码/波特率错。本项目五态落在 DataSource.cs，由 DeviceService 用连接时刻/最近字节/最近有效帧三个单调时间戳每秒评估。
- **数据质量码（MetricQuality）**：给单个设备的单个数值贴的可信度标签。比方：体检报告每个指标后标"本次有效 / 上次结果 / 已失效"。本项目 Good=新鲜、Stale=超期但无故障证据、Bad=从未收到或链路故障期间超期；写值瞬间置 Good，心跳只降级。
- **单调时钟（Environment.TickCount64）**：只增不减的毫秒计时器，不受用户改系统时间影响。本项目所有"多久没数据"的判据都用它，避免时间跳变造成误报/漏报。
- **ViewModel 翻译层**：Core 的枚举（LinkHealth/MetricQuality）只表达语义，不携带文案与颜色；翻译成中文短词在 ViewModel（ToStateText/ToLinkHealthText），翻译成颜色在 XAML DataTrigger。比方：Core 说"电压 220V"，UI 决定显示成绿色数字还是红色图标——换皮肤不改 Core。
- **Interlocked（原子操作）**：把"读-判断-改"合成一条 CPU 指令执行的工具类，别的线程看不到中间状态。比方：洗手间门牌"看牌+翻牌"是一个动作，不存在两人同时看到"无人"都进门的缝隙。本项目用于 DeviceService.cs 的 _reconnectRunningFlag（心跳线程与重连循环共享）：CompareExchange(x,1,0) 抢锁防重入、CompareExchange(x,0,0) 原子读、Exchange(x,0) 原子写。分工口诀：volatile 管"看得见"，Interlocked 管单变量"检查+改"，lock 管多语句临界区。
- **CancellationTokenSource / CancellationToken（取消令牌）**：协作式取消机制——"请你在下一个安全点停下来"。CTS 是遥控器（能按 Cancel()），Token 是发给工人的接收器（只能听不能按），用一次即作废，下次需 new 新的。比方：餐厅打烊铃——经理摇铃，正在等菜的员工立刻收工，而不是做完三小时才发现店关了。本项目重连循环靠它在切源/Dispose 时秒级退出：CancelReconnect 摇铃 → Task.Delay(ct) 抛 OperationCanceledException → return → finally 归零旗标。
- **Task / async / await**：.NET 异步编程三件套。Task = "取餐号"——活正在做，你攥着号纸**不占着厨房**（线程），厨房可以给别人炒菜；await = 拿着号纸让出当前线程，不阻塞 CPU，等活完成再回来接着跑（不是"站在厨房等"）；async = 编译器变身开关——标记方法让编译器生成"能暂停再接着跑"的状态机。Task.Delay(ms, ct) = 承诺"ms 后或收到 ct 取消信号时通知你"，谁先发生算谁赢，取消则抛 OperationCanceledException。**Task 终态三选一**：RanToCompletion（await 继续下一行）/ Canceled（await 抛 OCE）/ Faulted（await 重抛原异常）——恢复执行永远由 CLR 派线程回到断点，线程先查终态再决定走向；"取消后 return"这条路同样需要线程回来执行 catch 和 return。
- **ConfigureAwait(false)**：await 后的代码"不回到原始 SynchronizationContext 再跑"。典型场景：Task.Run 内的池线程无 UI 上下文，加不加效果一样；加了是防御性编程——防止代码被搬去 UI 线程环境时死锁。
- **SynchronizationContext（传菜通道）**：.NET 给"线程专属工作区"的抽象。UI 线程有专属通道（UI 控件只能在自己的通道里碰），池线程没有。await 默认会"回到启动时的通道"接着跑——如果启动时是UI线程，做完后代码会被拽回UI线程；ConfigureAwait(false) 就是"别拽我回去，在哪做完就在哪跑"，防止UI线程被占卡死。
- **句柄（Handle）**：操作系统给你的"资源编号纸条"。操作任何硬件/系统资源（串口、文件、窗口、网络端口）都要拿着这张纸条喊"帮我操作"，纸条里存着操作系统内部的资源索引。本项目中 SerialPort 打开串口时会拿到一个句柄（CreateFile返回），DisconnectAsync 会把句柄还给操作系统（CloseHandle）；半开状态的句柄会导致"端口被占用"错误，所以重连前必须先清旧句柄。
- **续体（continuation）**：await 挂起时，编译器把"断点之后的剩余代码"打包成的回调对象，任务完成后由 CLR 派线程调用它接着跑。比方：取餐号背面写着的"拿到餐后去哪、接着做什么"。本项目例：MainViewModel.cs L202 `await UpdateDevicesAsync()` 的续体 = 其后的 Dispatcher.BeginInvoke 块 + finally 归零旗标；ConfigureAwait(false) 管的就是续体落在哪个线程执行，与任务本身在哪跑无关。
- **热任务（hot task）**：C# 的 async 方法一被调用就**立即开始执行**（同步跑到第一个未完成的 await 才挂起、返回 Task），不需要任何人 await 它来"启动"。比方：下单即开火——厨房不等你取餐才开始炒菜。对比：Rust 的 async 是冷任务，没人 poll 就一行都不跑。本项目含义：`LoadMetricsAsync()` 即使没人 await，方法体也会真实执行到第一个 await 点。


