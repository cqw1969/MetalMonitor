# GLOSSARY

- **DI（依赖注入）**：对象不自己 new 依赖，由容器按注册表构造并注入。本项目容器在 App.xaml.cs 的 services 注册表。
- **IsAvailable / IsConnected**：能力维度（硬件/服务此刻存不存在）/ 状态维度（当前连没连上）。串口场景：IsAvailable=系统存在串口硬件，IsConnected=端口已打开。
- **InvalidOperationException**："当前状态下此操作无意义"的专用异常。本项目用于两处：DI 注册缺失时容器抛出；能力不可用时服务方法主动抛出。
- **构造时快照 vs 动态计算**：取值一次性定型 / 每次访问重新求值。快照省开销但会过期，动态计算始终新鲜但有查询成本。
- **数据源三态（DataSourceState）**：Online=正常；Faulted=能力在但本次连接失败（可重试）；Unavailable=系统根本没这能力（重试也无效）。与"用户选了什么"正交，故障只改状态不改选择。

