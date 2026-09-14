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

## T23-A 共享执行语义（幂等收据持久化）— 2026-09-13 完成

- **改动文件**：DatabaseMigrations（v2 request_receipts）、`RequestReceiptStore.cs`（新）、SqliteLibraryStore（收据方法）、`OperationDispatcher.cs`（收据中间件 + launch 崩溃歧义恢复 + `library.init`）、`PipeServer.cs`/WireMessages（握手 ClientName 回填到请求）、HostRuntime（State.DataDirectory、Library 可替换）、Cli/Mcp（`library init`、profiles create/update 幂等键）、测试 +5。
- **验证结果**：累计 234 项测试通过（+5）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t23a.md`。
- **实现要点**：收据按 (库实例, actor, 操作, 幂等键) 唯一；completed 收据为终态，重放返回原结果（RequestId 对齐本次请求）；同键不同摘要 → IdempotencyConflict；launch.execute 进程创建后立即写尝试引用，prepared 收据恢复按 PID+启动时间尽力核实，证明不了 → UnknownOutcome 且原键永不重启；`library.init` 自举豁免前置收据、建库后登记。
- **环境备注**：ClientName 由 PipeServer 从握手回填到每个请求（收据 actor/审计）；清理过本工作树测试残留的孤儿 GameLibrary.Host 进程后构建恢复正常。
- **未验证范围**：收据保留期限/淘汰与 DataEpoch 失效（T24/T27）；LaunchAttempt 持久化与 history 跨重启查询（T23-B）；scan.start 收据与 Job 同事务（T16）；权限模型/调用方鉴权（T23-B/T28）。
- **下一项**：T11（入库/忽略：候选状态机、accept 幂等、defer、抑制与撤销；候选落库）。

## T11 入库/忽略 — 2026-09-13 完成

- **改动文件**：DatabaseMigrations（v3 games/candidates/ignore_rules）、`LibraryCatalogStore.cs`（新：候选 upsert/晋升/审核转移/游戏卡片/忽略规则与抑制）、SqliteLibraryStore（转发）、`ScanCandidate.cs`（Candidates 列表）、`OperationDispatcher.cs`（扫描落库 + candidates accept/defer/ignore + games.list/get + ignores.list/create/remove）、Cli/Mcp（10 个新操作三入口映射）、Contracts（ImplementedOperations +10）、测试（CandidateReviewTests 5 + 守卫更新）。
- **验证结果**：累计 239 项测试通过（+5）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t11.md`。
- **实现要点**：候选按物理路径唯一落库，重扫刷新不重复建卡；重扫命中 observed 候选 → 合法双跳晋升 pendingReview（稳定观察）；accept 幂等返回既有 GameId（收据重放 + 同路径复用）；Revision 乐观校验；candidates.ignore 自动登记 ExactPath 规则；ignores.create 立即抑制既有待审核候选，remove 撤销后 ignored→observed（恢复提示的唯一途径）；抑制命中的新候选不入库。
- **守卫更新**：未实现操作示例 games.list→games.update；tools/list 断言 games_list 已实现。
- **未验证范围**：ConfirmedIdentity 身份指纹匹配（随 T14/T05-C）；稳定观察周期核对与事件推送（T16/T23-B）；Deferred 重新查看独立入口；候选分页/筛选（T16）。
- **下一项**：T16（ScanCoordinator：队列上限、事件折叠、周期核对、手动/后台互斥）或 T13（翻译配置三入口）。

## T24-A 可观测性/诊断（审计日志）— 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Host/Observability/AuditLogWriter.cs`（新）、`LogSanitizer.cs`（新）、`OperationDispatcher.cs`（全请求审计 + diagnostics.status/logs）、`JobManager.cs`（ActiveJobCount）、HostRuntime/夹具（注入 AuditLog）、Cli（`diagnostics status|logs --limit`）、Mcp（diagnostics_status/logs）、Contracts（+2）、测试（DiagnosticsTests 6）。
- **验证结果**：累计 245 项测试通过（+6）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t24a.md`。
- **实现要点**：业务审计 JSONL 与诊断日志分开保留（audit-*.jsonl）；固定结构化字段、参数原文不写入、路径按已知前缀脱敏（{dataDir}/{userProfile}）；轮转 10 MiB×10、保留 90 天（仅日志目录内，不影响库中收据）；diagnostics.status 报进程/库/审计统计/活动作业数；diagnostics.logs 分页读最近审计。
- **未验证范围**：diagnostics.export/cache_rebuild 与独立诊断日志管道（T24-B）；耗时分布指标（T16/T24-B）。
- **下一项**：T16（ScanCoordinator）或 T07（MToolAdapter，前置 T05/T06 已就绪）。

## T07 MToolAdapter — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Domain/Tools/MToolRecipe.cs`（新：证据分级/能力状态/类型化步骤/配方/能力声明）、`BatRecipeParser.cs`（新：极小 BAT 解析，Domain 无 IO）、`src/GameLibrary.Infrastructure/Tools/MToolAdapter.cs`（新：只读发现/断链/重映射/能力声明）、`OperationDispatcher.cs`（tools.discover，库根白名单收口）、Cli（`tools discover --path`）、Mcp（tools_discover）、Contracts（+1）、测试（BAT 解析 13 + MTool 发现 5）。
- **验证结果**：累计 263 项测试通过（+18）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t07.md`。
- **实现要点**：证据分级 Static/Generated/Cli/Measured 如实区分（生成脚本=Generated 未验证；CLI 协议仅登记候选）；不实现 BAT 解释器、不执行原脚本、超范围语法一律 Unsupported；BrokenRecipe 旧路径重映射不继承验证；副作用声明 canDeploy/canRollback=Unsupported、mayUseNetwork=Unknown，无沙箱假承诺。
- **未验证范围**：类型化 ProcessStep 的多步执行（随 T13 接入 LaunchExecutor）；CLI 协议本地实测（随 T13/T08 验证记录）；T08/T09 适配器复用同一模式。
- **下一项**：T16（ScanCoordinator）或 T12（Desktop 纵切，前置 T06/T11 已就绪）。

## T12 Desktop 纵切 — 2026-09-13 完成（实机运行验证通过）

- **改动文件**：`src/GameLibrary.Desktop/MainWindow.xaml`/`MainWindow.xaml.cs`（重写：连接宿主、初始化库/注册库根/扫描、游戏卡片、候选审核、错误栏）、`App.xaml.cs`（--data-dir 透传）。
- **验证结果**：累计 263 项测试通过；format 通过；Release 构建 0 警告 0 错误。**实机运行验证**：部署布局（宿主二进制与客户端同目录）→ CLI 驱动完整闭环（建库/注册根/两轮扫描/accept/ignore）→ Desktop 启动显示「已连接 · 库 Opened · 游戏 1 · 候选 4」→ UI 内点击「接受入库」GameB 入库（游戏 2）——UIA + 截图核验，报告：`artifacts/build-reports/2026-09-13-t12.md`。
- **实现要点**：仅经 HostClient（EnsureStartedAsync + IpcRequest），与 CLI/MCP 同库同契约；扫描经 scan.start 作业轮询；审核操作带幂等键与 expectedRevision；库根授权以「注册库根」显式动作呈现；数据落库重启保留。
- **未验证范围**：卡片规模与虚拟化（T15）；封面/图片（T14/T15）；托盘/通知（T18）；WPF 自动化 UI 测试未建立（本报告为实机 UIA 核验）。
- **下一项**：T16（ScanCoordinator）、T08/T09（其余适配器）或 T14（Metadata/Assets）。

## T15-A 游戏库 UI（Steam 库风格视图骨架）— 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Desktop/App.xaml`（Steam 色板资源字典 + 按钮模板 + 侧边栏样式）、`MainWindow.xaml`（顶栏/左列表/右详情/状态栏三区布局）、`MainWindow.xaml.cs`（Entry 统一侧边栏模型、选中联动详情、候选审核按钮排、打开目录）。
- **验证结果**：累计 263 项测试通过；format 通过；Release 构建 0 警告 0 错误。实机截图核验：深色主题、侧边栏选中高亮、候选详情与绿色「接受入库」按钮排、状态栏计数正常。报告：`artifacts/build-reports/2026-09-13-t15a.md`。
- **实现要点**：审核列表只含 pendingReview 候选（accepted 以游戏卡片呈现，与 CLI/MCP 审核语义一致）；全部操作仍只经 HostClient；详情面板含游戏 meta 与候选说明（忽略将登记 ExactPath 规则）。
- **未验证范围**：虚拟化/搜索/收藏/视图 API（T15 剩余）；封面图（T14）；自动化 UI 测试。
- **下一项**：T14（Metadata/Assets）。

## T14-A 资料编辑与封面图 — 2026-09-13 完成

- **改动文件**：DatabaseMigrations（v4 game_fields/game_assets）、`GameProfileStore.cs`（新：字段分层/事务 Revision/资产导入）、`OperationDispatcher.cs`（fields.set + assets.import/list/get + GameDto 升级）、Cli（`fields set`、`assets import/list/get`）、Mcp（fields_set、assets_*）、Contracts（+4）、Desktop（封面头图/编辑标题/导入封面）、测试（GameProfileTests 3）。
- **验证结果**：累计 266 项测试通过（+3）；format 通过；Release 构建 0 警告 0 错误。实机验证：CLI 导入封面+改标题后，Desktop 详情页头图渲染、标题来源 user 标注、侧边栏镜像同步。报告：`artifacts/build-reports/2026-09-13-t14a.md`。
- **实现要点**：字段用户层覆盖自动层（value=null=用户清空）；fields.set 以游戏卡片 Revision 乐观校验并镜像 games.title；封面资产复制入应用自有目录不反写游戏目录，导入即当前封面，受限预览 ≤1 MiB；fields.set/assets.import 接入收据中间件。
- **未验证范围**：fields.clear/reset、assets.choose/crop/reset/remove、metadata.preview/refresh、标签 Suppress（T14-B）；缩略图/LRU（T15/T26）。
- **下一项**：T16（ScanCoordinator）、T08/T09（其余适配器）或 T14-B（fields.clear/reset 与 metadata）。

## T14-B 资料补全（clear/reset/crop/metadata）— 2026-09-13 完成

- **改动文件**：`GameProfileStore.cs`（auto 层登记/reset/choose/resetCover/removeAsset/WriteAutoField）、`src/GameLibrary.Host/Tools/ImageCropper.cs`（新，System.Drawing.Common 10.0.12 像素裁切）、`OperationDispatcher.cs`（fields.clear/reset、assets.choose/crop/reset/remove、metadata.preview/refresh）、Directory.Packages.props、Cli/Mcp（+7 操作三入口）、测试（GameProfileLifecycleTests 3）。
- **验证结果**：累计 269 项测试通过（+3）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t14b.md`。
- **实现要点**：clear（user 层 value=null）与 reset（删用户层回 auto 值）语义分离；首次 set 前自动层登记保证 reset 可回退；crop 真实像素裁切产出新资产设为当前、越界拒绝；choose 校验 Revision 不递增；remove 仅限非当前引用自有副本；metadata.refresh 作业式只更新 AutoValue 不覆盖用户层。
- **未验证范围**：标签 Suppress（随标签系统）；资产孤儿文件回收（T24-B/T27）。
- **下一项**：T16（ScanCoordinator）或 T08/T09（其余适配器）。

## T16 ScanCoordinator 与事件流 — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Host/Scanning/EventStream.cs`（新）、`ScanCoordinator.cs`（新）、`ScanCandidatePersistence.cs`（新：统一落库路径）、HostRuntime（装配+核对执行）、`OperationDispatcher.cs`（events.read + scan.start 收据 + 手动互斥 + game.created 事件）、Cli（`events read`、scan start 自动幂等键）、Mcp（events_read、scan_start 可选键）、Contracts（+1）、测试（EventStreamTests 4 + 适配）。
- **验证结果**：累计 273 项测试通过（+4）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t16.md`。
- **实现要点**：事件环形队列 4096 上限 + 同实体 2 秒抖动折叠 + 游标增量读取（过期 CursorExpired）；周期核对默认 15 分钟（可注入），手动/后台互斥（标志+忙位）；核对与手动扫描共用候选落库路径（稳定观察/抑制/事件一致）；scan.start 同键重放返回原 jobId（补齐 T23-A 作业/收据口）。
- **未验证范围**：事件持久化跨重启（T23-B/T27）；watcher 事件源（T18）；核对预算细分与耗时指标（T24-B）；Coordinator 计数器接入 diagnostics。
- **下一项**：T08/T09（其余适配器）或 T15 剩余（搜索/收藏/虚拟化）。

## T08 RenpyThief 适配器与验证记录 — 2026-09-13 完成

- **改动文件**：`MToolRecipe.cs` 扩展（ToolVerificationStatus/Record/Rules + RenpyThiefCapability）、`src/GameLibrary.Infrastructure/Tools/RenpyThiefAdapter.cs`（新：只读发现/指纹/Guided 保底计划）、迁移 v5（verification_records）+ `VerificationStore.cs`（新）、`OperationDispatcher.cs`（verification 五 handler + tools.discover renpythief 分支）、Cli/Mcp（+7 操作三入口）、测试（状态机 5 + RenpyThief 4）。
- **验证结果**：累计 277 项测试通过（+8）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t08.md`。
- **实现要点**：Guided 保底（启动主程序、无参数、cwd=安装目录，状态 AwaitingUserInTool，不猜 CLI 协议）；双结论分开累积（游戏启动→SemiAutomatic；翻译生效+游戏启动→VerifiedAutomatic；仅开窗口不升级）；指纹绑定（变化→ToolChanged 失效重验）；权限与能力正交。
- **未验证范围**：本地隔离样本实测七项条件（需用户授权样本）；GeneratedLauncher/CLI Adapter 待验证分支；标签 Suppress。
- **下一项**：T09（Player/SteamAdapter）或 T15 剩余（搜索/收藏/虚拟化）。

## T09 Player/SteamAdapter — 2026-09-13 完成

- **改动文件**：`src/GameLibrary.Domain/Tools/KeyValuesParser.cs`（新：Valve KeyValues 最小解析器 + SteamRules appid 校验 + 能力声明）、`src/GameLibrary.Infrastructure/Tools/SteamAdapter.cs`（新：注册表只读/常见路径发现、appmanifest 解析、-applaunch 模板）、`PlayerAdapter.cs`（新：常见播放器发现+参数模板）、`OperationDispatcher.cs`（ToolsDiscover 重构为四分支 mtool/renpythief/player/steam）、ErrorCodes（+SteamManifestMissing）、测试（KeyValuesParserTests 8 + PlayerSteamTests 5）。
- **验证结果**：累计 295 项测试通过（+18）；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t09.md`。
- **实现要点**：appid 仅取自本地 appmanifest 且校验十进制正整数（前导零/非数字拒绝）；无清单 → SteamManifestMissing 标注，不凭目录名硬填；-applaunch 为 Valve 公开稳定参数可生成模板，steam:// 协议未本机验收不生成；播放器模板 = exe + [目标路径]（公开稳定行为），参数差异须逐播放器样本验证；Steam 安装经注册表只读 SteamPath + 常见目录回退；steam 分支不收调用方路径（自动探测），player/mtool/renpythief 路径仍经库根白名单。
- **未验证范围**：steam:// 的 Shell 集成验收（策划案明示待验收）；播放器逐样本验证与 ExternalPlayer 配置（T13）；多 Steam 库 libraryfolders.vdf（T25）。
- **下一项**：T15 剩余（搜索/收藏/虚拟化）、T13（翻译配置三入口）或 T17（Reconcile/Identity）。

## T15-B 游戏库 UI（搜索/收藏/虚拟化/图像管理）— 2026-09-13 完成

- **改动文件**：迁移 v6（games.favorite）、`LibraryCatalogStore.cs`（SetFavorite/BuiltInViews/Favorite 字段）、`OperationDispatcher.cs`（games.update + views.list/get/activate + games.list 过滤 + game.updated/view.activated 事件）、Desktop（视图切换/搜索防抖/收藏按钮/封面 LRU+取消/虚拟化/app.manifest PerMonitorV2/Ctrl+F）、测试适配。
- **验证结果**：累计 295 项测试通过；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-13-t15b.md`。
- **实现要点**：收藏为 games.update 受限字段（契约 note）+ Revision 乐观校验；views.activate API 控制激活视图（内存态）；games.list 支持 favorite/search 参数（agent 直用）；Desktop 搜索 300ms 防抖客户端过滤、封面 LRU 32 张+切换取消加载、列表虚拟化 Recycling、PerMonitorV2 DPI、Ctrl+F。
- **未验证范围**：自定义视图 CRUD 与视图持久化（T15-C）；5000 条/200 查询性能基线（T26）；UI 自动化测试。
- **下一项**：T13（翻译配置三入口）或 T17（Reconcile/Identity）。

## T13 翻译配置三入口 — 2026-09-14 完成（接手 GitHub main 2ed6656 后首个任务）

- **改动文件**：`DatabaseMigrations.cs`（v7：translation_inherited/translation_override）、`LibraryCatalogStore.cs`（GameCard 翻译字段/SetTranslationOverride）、`SqliteLibraryStore.cs`（SetFavorite/SetTranslationOverride 转发）、`LaunchRegistry.cs`（ToolId/IsDefault/SetDefault/RemoveProfile/计划查询）、`OperationDispatcher.cs`（translation.get/set、games.update、profiles.set_default/remove/validate、launch Required 不回退、accept 写 inherited）、Contracts（ImplementedOperations +6）、Cli/Mcp（+6 操作三入口）、`tests/.../Translation/TranslationConfigTests.cs`（15 项）+ 4 处过时断言更新。
- **验证结果**：累计 310 项测试通过；format 通过；Release 构建 0 警告 0 错误。报告：`artifacts/build-reports/2026-09-14-t13.md`。
- **实现要点**：继承值与用户覆盖分离持久化；Required 不回退——plan 返回 needsUserAction+计划预览，execute 拒绝 TranslationRouteUnavailable，唯一回退路径是显式 translation.set(NotRequired)；profiles.remove 对默认配置拒绝并给 RecoveryOperation；games.update 未知字段拒绝。
- **接手发现**：T15-B 声称的 games.update/views.* 实际未实现（Desktop 收藏/视图切换不可用、无测试覆盖）；本提交补齐 games.update，views.* 登记为 T15-C 待办。
- **未验证范围**：T17 对账刷新 inherited；views.*（T15-C）；工具指纹联动 validate；Desktop 界面接入新操作。
- **下一项**：T15-C（views 补全 + Desktop 接入 translation/games.update）或 T17（Reconcile/Identity）。

## T15-C 视图补全与 Desktop 翻译接入 — 2026-09-14 完成

- **改动文件**：`DatabaseMigrations.cs`（v8：library_views）、`LibraryViewStore.cs`（新）、`SqliteLibraryStore.cs`（视图转发 + InsertGame favorite 修复）、`HostRuntime.cs`（ActiveViewId）、`OperationDispatcher.cs`（views 六操作 + games.list viewId/sort）、Contracts（+6）、Cli/Mcp（+6 三入口）、Desktop（翻译策略行 + 切换按钮 + views.activate 幂等键修复）、`tests/.../Translation/ViewsTests.cs`（6 项）。
- **验证结果**：累计 316 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t15c.md`。
- **实现要点**：内置/自定义视图统一语义（search/favoriteOnly/sort）；激活为宿主内存态 + view.activated 事件；games.list 支持 viewId 直查；修复 T15-B 遗留的 views.activate 缺幂等键（Desktop 视图切换恢复可用）与 InsertGame 忽略 Favorite 的缺陷。
- **未验证范围**：activeViewId 持久化（随 settings.*）；Desktop 服务端视图过滤接入；UI 自动化。
- **下一项**：T17（Reconcile/Identity）或 T18（通知/托盘）。

## T17 Reconcile/Identity — 2026-09-14 完成

- **改动文件**：`DatabaseMigrations.cs`（v9：availability/missing_since_utc）、`LibraryCatalogStore.cs`（GameCard 可用性字段/UpdateAvailability/RelinkGame）、`SqliteLibraryStore.cs`（转发）、`Scanning/ReconcileService.cs`（新）、`ScanCandidatePersistence.cs`（BackupHint/DuplicateHint）、`OperationDispatcher.cs`（games.relink + 核对接线 + games DTO 可用性）、HostRuntime（周期核对接线）、Contracts（+1）、Cli/Mcp（relink 三入口）、`tests/.../ReconcileIdentityTests.cs`（8 项）。
- **验证结果**：累计 324 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t17.md`。
- **实现要点**：缺失两次核对 ≥60 秒才 Missing、离线不累计、恢复重计数（ID-04/05）；可用性写回不占 Revision；relink 仅改 DB + 库根白名单/冲突/存在性校验；副本与备份命名只提示不排除。
- **未验证范围**：指纹驱动自动 relink 建议（T25）；watcher 重命名事件（T18）；真实 ACL accessError 夹具（T25）。
- **下一项**：T18（通知/托盘）或 T13 遗留的 settings.*。

## T18 通知/托盘/退出语义 — 2026-09-14 完成

- **改动文件**：`DatabaseMigrations.cs`（v10：notification_batches）、`NotificationStore.cs`（新：批生成/迁移）、`SqliteLibraryStore.cs`（转发）、`ScanCandidatePersistence.cs`（通知生成接线）、`OperationDispatcher.cs`（notifications 四操作 + host.stop 控制面解耦）、HostRuntime/Program（停机回调 + 延迟停机）、Desktop（托盘三菜单 + 关闭缩托盘）、Contracts（+5）、Cli/Mcp（+5 三入口）、`NotificationTests.cs`（5 项）+ `HostStopE2ETests.cs`（真实进程）。
- **验证结果**：累计 330 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t18.md`。
- **实现要点**：ack≠accept（响应带 nextActions 引导显式候选决定）；acknowledged/deferred 批不复活；host.stop 不依赖业务库、响应送达后延迟停机；关闭缩托盘、"退出界面"与"停止后台并退出"语义分离。
- **未验证范围**：Windows 气泡/全屏专注模式（需实机手测）；开机启动（settings.*）。
- **下一项**：settings.*（三入口设置 + 视图激活持久化）或 T19 前的补全任务。

## settings.* 三入口 + 持久化 + 开机启动 — 2026-09-14 完成

- **改动文件**：`DatabaseMigrations.cs`（v11：app_settings）、`SettingsStore.cs`（新）、`StartupShortcutManager.cs`（新：启动文件夹 .lnk，目录可注入）、`SqliteLibraryStore.cs`（转发）、`OperationDispatcher.cs`（settings 三操作 + views.activate/remove 持久化激活视图）、HostRuntime（启动读取设置：恢复激活视图 + 核对间隔生效）、Host/Program（日志提供程序显式化修复启动崩溃）、Contracts（+3 操作、+ConfigurationInvalid）、Cli/Mcp（+3 三入口）、`SettingsTests.cs`（7 项）。
- **验证结果**：累计 337 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-settings.md`。
- **实现要点**：受限字段 patch（未知拒绝）；开机启动走启动文件夹快捷方式（不碰注册表/服务/环境变量）；activeViewId 三处写一致 + 启动恢复；扫描间隔启动时生效。
- **未验证范围**：theme/closeToTray 的 Desktop 消费；实机重启验证开机启动；间隔动态调整需重启。
- **下一项**：T23-B/T24-B 语义收尾，或 T26/T27 性能与恢复演练。

## T23-B 事件持久化与游标恢复 — 2026-09-14 完成

- **改动文件**：`DatabaseMigrations.cs`（v12：event_records）、`EventRecordStore.cs`（新：UPSERT/裁剪/按 epoch 读取）、`EventStream.cs`（构造注入 store、发布落库、序号从库恢复、读取以持久层为准）、`SqliteLibraryStore.cs`（DatabaseConnection 访问器）、HostRuntime/PipeRoundtrip 夹具（传入 store）、`EventStreamTests.cs`（+2：跨重启回放/epoch 失效）。
- **验证结果**：累计 339 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t23b.md`。
- **实现要点**：发布同步落库（折叠 UPSERT 同序号，与 T16 游标语义一致）；序号跨重启单调；events.read 库优先 + 当前 epoch 过滤 + CursorExpired；保留 7 天/100,000 条惰性裁剪；落库失败不阻塞业务。
- **未验证范围**：恢复演练端到端（REC-02）；裁剪性能（T25）。
- **下一项**：T24-B（诊断指标深化）或 T26/T27。

## T24-B 诊断指标深化 — 2026-09-14 完成

- **改动文件**：`Observability/HostMetrics.cs`（新）、`EventStream.cs`（OnPublished 回调 + OccupiedSlots/OverflowedCount）、`JobManager.cs`（OnJobFinished 回调）、HostRuntime（指标实例 + 三处接线）、`OperationDispatcher.cs`（RecordRequest + diagnostics.status 扩展 metrics/eventStream 段）、PipeRoundtrip 夹具适配、`MetricsTests.cs`（2 项）。
- **验证结果**：累计 341 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t24b.md`。
- **实现要点**：请求延迟/错误码计数与审计同源同值；事件发布/折叠/槽淘汰计数；作业终态计数；diagnostics.status 新增 metrics 与 eventStream 段。
- **未验证范围**：SQLite 提交延迟/首屏/UI 帧时长/图片缓存命中（T26）；枚举吞吐（T25）；指标跨进程汇总。
- **下一项**：T26/T27 性能与恢复演练，或 T28 覆盖审计。

## T27 故障恢复演练（第一轮 REC-01/03/04）— 2026-09-14 完成

- **改动文件**：`OperationDispatcher.cs`（CacheRebuild 实现）、contracts（cache_rebuild execution=sync 更正）、Contracts（+1 操作）、Cli/Mcp（+1 三入口）、`FaultRecoveryTests.cs`（5 项：REC-01 中段迁移失败、REC-03 配置损坏回落+缓存重建不触碰原图、REC-04 三场景收据边界）。
- **验证结果**：累计 346 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t27.md`。
- **演练结论**：多步迁移中段失败停在最后成功版本且可修复续升；配置损坏可自愈；launch 崩溃歧义三边界（可继续/UnknownOutcome-孤儿/UnknownOutcome-死亡）均不重复启动。同键异参 → IdempotencyConflict 分支另行覆盖。
- **未覆盖**：REC-02（依赖 backups.restore 实现）、REC-05（随 T19 打包）、真机 kill 注入（T30）。
- **下一项**：T26/T25 性能基线或 T28 覆盖审计。

## T28 三入口覆盖审计 — 2026-09-14 完成

- **改动文件**：`tests/.../Coverage/ThreeEntranceCoverageTests.cs`（4 项门禁）、`src/GameLibrary.Cli/CommandLine.cs`（补 9 个 CLI 映射缺口）、`docs/coverage.md`（覆盖报告）、IntegrationTests 引用 Cli/Mcp。
- **验证结果**：累计 350 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t28.md`。
- **审计结论**：71 操作三入口齐备并由 AI-12 门禁锁定；审计抓到并修复 9 个 CLI 映射缺口（fields clear/reset、metadata ×2、assets choose/crop/reset/remove、diagnostics cache-rebuild——此前 handler+MCP 在但 CLI 报"未知命令"）；58 个 planned 操作明确登记归属。
- **未验证范围**：真实第三方 MCP 客户端全操作遍历（T29）；planned 操作按归属任务实现。
- **下一项**：T25/T26 性能基线（需夹具生成器）或 T29 协议边界检查。

## T29 协议与边界检查 — 2026-09-14 完成

- **改动文件**：`tests/.../Coverage/ProtocolBoundaryTests.cs`（10 项：路径攻击矩阵 ×6、重关联穿越/分段边界、资源越界 ID、审计隐私、未知字段攻击）。
- **验证结果**：累计 356 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t29.md`。
- **边界结论**：路径攻击双层拦截（GamePath + 库根白名单）；审计无参数原文；未知字段/越界 ID 全部拒绝；未发现需修补的产品缺陷（本轮）。
- **未验证范围**：权限运行时强制（随权限模型）；真实第三方 MCP 客户端遍历（现有 stdio 覆盖主路径）。
- **下一项**：T25/T26 性能基线或 T19/T30 发布验收。

## backups.* 五操作 + REC-02 恢复演练 — 2026-09-14 完成

- **改动文件**：`DatabaseMigrations.cs`（v12：event_records，T23-B）、`Infrastructure/Backups/`（BackupArchive/ControlAreaStore）、`SqliteLibraryStore.cs`（DatabaseConnection 访问器）、`HostLibraryState.cs`（Store 可写）、`OperationDispatcher.cs`（backups 五 handler + PipeServer 容错 + 响应属性冲突修复）、`HostClient/HostConnection.cs`（InvokeAsync 断连重连重试）、Cli/Mcp（+6 三入口）、`BackupsTests.cs`（6 项）、`EventStreamTests.cs`（+2 持久化回放）。
- **验证结果**：累计 366 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-backups.md`。
- **实现要点**：备份 = 库快照+原图+哈希清单；restore = 安全备份→关连接→暂存替换→重开校验→原图恢复→dataEpoch 续期；控制收据/维护日志在控制区不随库回滚；同键重试重放原结果不再覆盖（REC-02）；HostClient 断连重连重试（AI-03）。
- **过程修复**：PipeServer 接受循环无容错（单次异常宿主假死）；inspect 响应 JSON 属性名冲突导致连接断开。
- **未验证范围**：恢复中途 kill 进程的续演（T30）；event_records 裁剪性能（T25）。
- **下一项**：T25/T26 性能基线或 T19/T30 发布验收。

## T25 扫描性能基线（缩减规模 1/10）— 2026-09-14 完成

- **改动文件**：`tests/.../Performance/ScanPerformanceTests.cs`（PERF-01 吞吐/内存 + PERF-02 取消 P95）、`artifacts/perf/scan-perf.md`（基线数据）。
- **验证结果**：累计 368 项测试通过；format 通过。报告：`artifacts/build-reports/2026-09-14-t25.md`。
- **关键数字**：热扫 429 ms / 1,000 目录（≈2,329 目录/秒）；内存增量 0 MiB（≤256 MiB 达标）；取消停止 P95 = 2 ms（≤2000 ms 达标）。
- **如实声明**：缩减规模 1/10，全量 10 万文件版用同一生成器在 T30 前复核。
- **下一项**：T19/T30 发布验收。
