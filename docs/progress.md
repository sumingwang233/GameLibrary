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

## T04 边界与分类 — 2026-09-13 完成（A 状态与分类 / B LNK 两个提交）

- **改动文件**：`src/GameLibrary.Domain/States/StateMachines.cs`（四正交枚举+转移表+缺失两次核对 tracker）、`src/GameLibrary.Domain/Classification/`（ClassificationRules/TranslationPolicy）、`src/GameLibrary.Infrastructure/Shell/`（ShellLinkInterop/LnkResolver）、单元 +28、集成 +6（LNK 夹具全用 Windows 接口生成）。
- **验证结果**：累计 202 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-13-t04.md`。
- **实测结论**：Windows Shell 保存快捷方式时即把链解析到最终目标，原生接口无法构造循环/自指 LNK；解析器 Cyclic 分支保留为对第三方原始目标的防御，未用伪结构夹具伪造。
- **未验证范围**：Duplicate/Backup/Broken 提示标记（T17）；嵌套候选与合集判定（T05）。
- **下一项**：T05（原生扫描接口：scan start/inspect/coverage、候选查询与 Job 状态同步提供 CLI/MCP；替代 Probe）。

## T05-A 原生扫描接口（宿主内编排）— 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Host/Hosting/JobManager.cs`（新）、`src/GameLibrary.Host/Scanning/ScanJobRunner.cs`（新，仅遍历与覆盖；检测与候选编排在 T05-B）、Contracts（OperationCatalog/ErrorCodes：scan.start/status/cancel/coverage、jobs.get、NotFound）、Host（OperationDispatcher/HostRuntime 接线）、Cli（scan/jobs 命令，自动拉起宿主）、Mcp（scan_start/scan_status/scan_cancel/scan_coverage/jobs_get 工具）、测试（JobManagerTests、ScanOperationTests 进程内回环、ScanE2ETests 真实宿主进程 CLI/MCP E2E）。
- **验证结果**：累计 215 项测试通过（+13）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t05a.md`。
- **实现要点**：JobManager 注册表 + JobContext 进度通道 + 终态判定（succeeded/failed/cancelled）；扫描在宿主内执行，CLI/MCP 只做单次调用与轮询；覆盖报告经 coverage 数据实时可查；E2E 测试按 PID 精确清理宿主与自身 MCP 进程。
- **环境备注**：本任务起开发转入 Qoder 工作树（GitHub：sumingwang233/GameLibrary 私有仓库，origin/main）；D 盘原仓库遗留的 T05-A 未提交改动已导入并修复 ScanE2ETests 的进程清理缺陷（`using var` 位于 try 内导致成功路径也抛 "No process is associated"）。
- **未验证范围**：取消/暂停的真实进程级注入；检测器接入与候选编排、候选查询操作（T05-B）；事件推送与 Job 状态变更通知（T23）。
- **下一项**：T05-B（检测器接入扫描作业、候选编排与查询操作）。

## T05-B 候选编排与查询 — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Host/Scanning/ScanCandidate.cs`（新：ScanCandidate/CandidateRegistry/ScanCandidateCollector）、`DirectoryWalker.cs`（可选 onDirectory 回调）、`JobManager.cs`（JobContext.JobId）、`ScanJobRunner.cs`（逐目录编排）、`HostRuntime.cs`（State.Candidates）、`OperationDispatcher.cs`（scan.inspect/candidates.list/candidates.get）、Contracts（ImplementedOperations +3）、Cli（`scan inspect`、`candidates list|get`）、Mcp（scan_inspect/candidates_list/candidates_get）、测试（编排 6 + 管道 2 + E2E 扩展）。
- **验证结果**：累计 223 项测试通过（+8）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t05b.md`。
- **实现要点**：≥Medium 引擎证据的目录产生候选；双 high 记 EngineConflict 不取先注册；确认根内独立证据 → NestedCandidate；≥2 直属 GameRoot 的父目录 → Container（记子根数）；祖先分类继承仅取扫描根内段；候选宿主内存态（Observed 起步）。
- **未验证范围**：accept/defer/ignore 与候选落库（T11）；Flash 每文件拆卡与 LNK 入口核实（随 T07/T05-C）；取消/暂停下候选部分性注入测试（T25/T27）。
- **下一项**：按依赖图 T06（启动计划/执行器与桩）或 T23（共享执行语义，T05 的前置补全）。

## T06 启动计划/执行器与桩 — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Host/Launching/LaunchRegistry.cs`（新：Profile/Plan/Attempt + 执行器）、`OperationDispatcher.cs`（profiles.*4 + launch.*4 handler）、Contracts（ImplementedOperations +8）、Cli（profiles/launch 命令，--arg 可重复、--idempotency-key、--plan-id/--profile-id）、Mcp（profiles_*4、launch_*4 工具）、TestProcessStub（--hold-ms）、测试（LaunchOperationTests 5 + LaunchE2ETests 1 + 契约守卫更新）。
- **验证结果**：累计 230 项测试通过（+7）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t06.md`。
- **实现要点**：全入口互斥按游戏维度（进行中启动拒绝第二次 execute，观察退出释放）；幂等键重放返回原尝试；Profile Revision 使旧计划 PlanStale；execute 只接受经 Profile 四道校验的 exe/cwd，任意 EXE 路径不能绕过 Profile；不等待游戏退出，观察在查询时尽力刷新。
- **L3 审计修复（CWE-22 路径包含，high）**：推送前 L3 深度审查发现调用方路径直达进程执行/文件系统读取。新增 `RootRegistry`（库根白名单，`src/GameLibrary.Host/Scanning/RootRegistry.cs`，GamePath 规范化 + 重解析点拒绝 + 前缀包含 + 幂等注册）与 `roots.add`/`roots.list` 三入口映射；`scan.start`/`scan.inspect`/`profiles.create`/`profiles.update` 在 dispatcher 边界统一收口，白名单外返回 `PermissionDenied`；测试 +1（白名单外拒绝用例）并更新全部路径类测试。
- **守卫更新**：契约测试 `SchemaFiles_AreNotYetClaimedAsImplemented` 按"实现即事实"解除 launch.execute 断言（T22 骨架守卫）。
- **未验证范围**：启动收据持久化与崩溃恢复（UnknownOutcome）随 T23/T27；Profile 默认配置/工具绑定/翻译策略随 T13；PlanExpired/事件推送随 T16/T24。
- **下一项**：T11（入库/忽略，依赖 T05/T10/T23）前可先补 T23（共享执行语义：收据/Revision/Job 取消事件）。
