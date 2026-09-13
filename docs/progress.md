# 开发进度登记

按《开发Agent任务书》要求，每完成一项任务在此登记：任务 ID、改动文件、验证结果、未验证范围、下一项。

## T00 工程基线 — 2026-09-13 完成

- **环境**：安装 .NET SDK 10.0.401 至用户目录 `C:\Users\sumingwang\.dotnet-sdk-10.0`（系统原仅有运行时无 SDK；未修改环境变量，`global.json` 锁定版本）。
- **改动文件**：`global.json`、`NuGet.config`、`Directory.Build.props`、`Directory.Packages.props`、`.editorconfig`、`.gitignore`、`AGENTS.md`、`GameLibrary.slnx`、`src/` 9 个工程、`tests/` 6 个工程。
- **验证结果**：Release 构建 0 警告 0 错误；`dotnet format --verify-no-changes` 通过；12 项测试全部通过（架构依赖方向 8 + 信封契约 4）。报告：`artifacts/build-reports/2026-09-13-t00.md`。
- **未验证范围**：Desktop 窗口运行时交互；空测试工程无用例；未创建/连接任何数据目录。
- **下一项**：T20 架构契约/ADR。

## T20 架构契约/ADR — 2026-09-13 完成

- **改动文件**：`docs/adr/0001..0007`、`contracts/operations.v1.json`（29 命名空间 129 操作，全部已注册、实现状态由代码声明）、`contracts/README.md`（命名规则/治理/负责人）、`tests/GameLibrary.ContractTests/OperationCatalogTests.cs`。
- **验证结果**：契约测试 9 通过 / 0 失败（信封 4 + 目录 5：矩阵完整性、命名规则、标识符唯一、字段合法性）；`dotnet format` 通过。报告：`artifacts/build-reports/2026-09-13-t20.md`。
- **未验证范围**：schemas/*.json 与适配器能力类型化契约随对应任务交付；尚无任何已实现 handler。
- **下一项**：T01 Domain 路径与身份。

## T01 Domain 路径与身份 — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Domain/Paths/`（GamePath/PathRejectReason/PathValidationResult）、`src/GameLibrary.Domain/Identity/`（GameId/MatchFingerprint）、`tests/GameLibrary.UnitTests/`（50 项用例）、Contracts ErrorCodes 增补 InvalidPath/UnsupportedPath。
- **验证结果**：累计 67 项测试通过（单元 50 + 契约 9 + 架构 8）；`dotnet format` 通过。报告：`artifacts/build-reports/2026-09-13-t01.md`。
- **实现要点**：四键分离（PhysicalPath/ComparisonKey/GameId/MatchFingerprint）；比较键保留原字符、OrdinalIgnoreCase、`.`/`..` 解析、策略版本化；11 类非法输入明确拒绝；UNC 与设备命名空间标记为 Unsupported；分段边界前缀比较；无 260 限制、无 NTFS ID 依赖。
- **未验证范围**：长路径/exFAT 实盘端到端（T02/T25）；指纹摘要采集（T02/T03）。
- **下一项**：T21 配置/Host/HostClient。

## T21 配置/Host/HostClient — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Contracts/Ipc/`（DataDirectory/ChannelNames/IpcFrame/WireMessages）、`src/GameLibrary.HostClient/HostConnection.cs`、`src/GameLibrary.Host/`（SingleInstanceGuard/PipeServer/OperationDispatcher/HostRuntime/Program）、`tests/GameLibrary.ContractTests/`（DataDirectory/IpcFrame，+17）、`tests/GameLibrary.IntegrationTests/`（+9）。
- **验证结果**：累计 97 项测试通过；`dotnet format` 通过；真实 Host 进程 E2E 通过（冷启动/握手/host.status/第二实例退出码 7）。报告：`artifacts/build-reports/2026-09-13-t21.md`。
- **实现要点**：数据目录词法规范化 + SHA-256 派生管道/互斥名（路径别名归一）；4MiB 长度前缀帧；同用户管道（CurrentUserOnly）；互斥键+目录锁文件双守卫；host.status 为首个可用 handler，未实现操作统一 UnsupportedOperation。
- **未验证范围**：客户端引导拉起宿主（T22）；提权/跨用户拒绝矩阵（T29）；断连故障注入（T23）。
- **下一项**：T22 CLI/MCP 原生骨架。

## T22 CLI/MCP 原生骨架 — 2026-09-13 完成

- **改动文件**：Contracts（OperationCatalog + 嵌入目录）、Host（capabilities.get/schema.get handler、StdioDetach/--detach-stdio）、HostClient（HostProcessLauncher）、Cli（完整命令行骨架）、Mcp（官方 SDK 2.2.0 stdio 服务端 + 工具）、HeadlessE2ETests（+7）。
- **验证结果**：累计 105 项测试通过（含真实 stdio MCP 客户端与 CLI 进程契约）；format 通过。报告：`artifacts/build-reports/2026-09-13-t22.md`。
- **实现要点**：目录单一来源嵌入程序集，可用性由代码声明（tools/list 不列未实现）；CLI JSON/退出码/stderr 分离；宿主拉起用 ShellExecute 避免句柄继承（修复管道捕获挂死缺陷）+ --detach-stdio。
- **未验证范围**：MCP Resources 模板、退出码全分支、跨用户/提权矩阵、断连注入（随 T23/T29 与首个数据操作落地）。
- **下一项**：按依赖图，T10（Persistence 基础，前置 T01/T20/T21 已就绪）。

## T10 Persistence 基础 — 2026-09-13 完成（A/B/C 三个小提交）

- **改动文件**：`src/GameLibrary.Infrastructure/Persistence/`（SqliteLibraryStore/DatabaseMigrations/Options/LibraryOpenResult）、`src/GameLibrary.Host/`（HostLibraryState/HostRuntimeState、HostRuntime.StartAsync 打开库、握手与 host.status 上报真实库状态）、`tests/GameLibrary.IntegrationTests/Persistence/`（15 项）+ 带库宿主 E2E（1 项）。
- **验证结果**：累计 121 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-13-t10.md`。
- **实现要点**：WAL/外键/busy_timeout/Pooling=False；schema_info 版本；损坏与 0 字节不建空库；连续版本单事务迁移+迁移前快照+失败不半升级；SQLite 备份 API 一致快照；dataEpoch 更换持久化；Host 唯一连接所有者。
- **未验证范围**：迁移边界故障注入与恢复控制区（T27）；业务表/实体 Revision 随 T11/T23 追加迁移；RecoveryRequired 操作级限制随权限模型（T23）。
- **下一项**：T02（Filesystem：分段/取消/暂停、安全游标、规则优先级和覆盖字段）。

## T02 Filesystem — 2026-09-13 完成（A 规则引擎 / B 枚举器 两个提交）

- **改动文件**：`src/GameLibrary.Domain/Scan/`（ScanRule/ScanRuleMatcher/ScanRuleSet）、`src/GameLibrary.Infrastructure/Scanning/DirectoryWalker.cs`、`tests/GameLibrary.UnitTests/Scan/`（21 项）、`tests/GameLibrary.IntegrationTests/Scanning/`（7 项）。
- **验证结果**：累计 149 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-13-t02.md`。
- **实现要点**：受限 matcher（无正则执行）；SystemMandatory 排除不可绕过；预算分段+续扫游标（合计=完整扫描）；取消/暂停检查点；重解析点不跟随；目录级排除剪枝；分支问题分类与覆盖计数；有缺口即 partial。
- **未验证范围**：真实重解析点/ACL/长路径夹具（T03/T25）；每目录 256 读取上限属检测器阶段（T03）；汇总与分页（T16）。
- **下一项**：T03（检测器：五类首批引擎/格式、正负/不可读证据、置信度、确定冲突消解）。

## T03 检测器 — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Domain/Detection/`（证据模型/快照端口/检测器集合/评分规则 + Unity/RPG MV-MZ/Ren'Py/Kirikiri/Flash 五检测器）、`src/GameLibrary.Infrastructure/Scanning/FileSystemDirectorySnapshot.cs`、`tests/GameLibrary.UnitTests/Detection/`（14 项）、`tests/GameLibrary.IntegrationTests/Scanning/EngineDetectorFixtureTests.cs`（5 项）。
- **验证结果**：累计 168 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-13-t03.md`。
- **实现要点**：三态证据（不可读≠缺失≠负向）；配对才 high；双 high → EngineConflict 不取先注册；报告与注册顺序无关；卸载器/崩溃处理器等永不成为入口；检测器仅经只读快照端口。
- **未验证范围**：其余引擎批次（T04+）；完整合法 SWF 样本与固定 hash（T25）；System.json 标题读取（T14）；嵌套候选/合集判定（T04）。
- **下一项**：T04（边界与分类：正交 CandidateKind/ReviewState/Availability、toolNeed 覆盖、合法/循环/失效 LNK）。
