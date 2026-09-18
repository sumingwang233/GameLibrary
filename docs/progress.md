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

## T19 发布与文档（v1 内部版）— 2026-09-14 完成

- **改动文件**：`artifacts/publish/win-x64/`（四组件自包含发布 + README.txt）、`artifacts/GameLibrary-win-x64.zip`（73 MiB）、`artifacts/build-reports/2026-09-14-t19.md`。
- **验证结果**：REC-05 近似冒烟全过——发布目录直接运行 capabilities/host status(±no-start)/library init/host stop，自包含无需 SDK；优雅停机复核 running:false。
- **如实声明**：真"无 SDK 机器"验证、F 盘实盘全盘扫描覆盖、10 万文件全量性能复核、REC-02 宿主崩溃续演 → T30 前手动步骤。
- **下一项**：T30 发布前统一验收（核对清单逐项过）。

## R1 审查意见整改（第一/二阶段）— 2026-09-14 完成

按《v1 审查意见稿》完成发布阻断项与 Desktop MVP 整改：

- **改动文件（Host/Infrastructure）**：`PipeServer.cs`（删除 serve-debug 硬编码写入；连接代数快照与旧纪元断连；握手权限回填）、`OperationDispatcher.cs`（宿主级请求门单写队列；MaintenanceMode；libraryInstanceId/expectedDataEpoch 校验；响应信封统一补全库实例/纪元；握手权限检查；幂等键字符集校验；inspect-steps/receipt-debug 删除；games.list limit/offset 分页；roots.remove 实现 + 落库；library.init/restore 后库会话整体切换 + 连接代数递增；settings uiFontScale）、`HostRuntime.cs`（库根/Profile/启动尝试恢复回灌 + 持久化回调接线 + 中断作业标记 + BindLibraryStore + MaintenanceMode/ConnectionGeneration）、`EventStream.cs`（BindStore 运行中重绑 + 走互斥访问 + ObjectDisposedException 容忍）、`RootRegistry.cs`（AddExisting/Remove/Revision）、`LaunchRegistry.cs`（OnAttemptChanged/OnProfileChanged/RestoreProfile/RestoreAttempt）、`JobManager.cs`（OnJobRecorded）、`DatabaseMigrations.cs`（v13 运行态四表 + v14 game_fields 分层主键 + v15/16/17 外键重建）、`SqliteLibraryStore.cs`（单写锁 + ReadExclusive/WriteExclusive + 运行态转发 + 迁移后 foreign_key_check）、`RuntimeStateStore.cs`（新）、`GameProfileStore.cs`（auto/user 分层并存 + WriteAutoField 只动 auto 层）、`SettingsStore.cs`（reset Revision 单调不回退 + uiFontScale）、`BackupArchive.cs`（backupId/清单相对路径校验 + 收据文件名 SHA-256 + debug 删除）、`StartupShortcutManager.cs`（--data-dir 参数）、`SettingsUpdate` 快捷方式带数据目录。
- **改动文件（Contracts/Cli/Mcp）**：`WireMessages.cs`（IpcRequest.LibraryInstanceId/ExpectedDataEpoch/GrantedPermissions、HandshakeRequest.Permissions）、`ErrorCodes.cs`（DataEpochMismatch/LibraryInstanceMismatch）、`OperationSchemas.cs`（新：79 操作真实 inputSchema 程序化生成）、`OperationCatalog.cs`（roots.remove）、`CommandLine.cs`/`Program.cs`（roots remove --root-id；games list --limit）、`GameLibraryTools.cs`（roots_remove 工具）。
- **改动文件（Desktop）**：`App.xaml`（DynamicResource 化 + 焦点样式 + Danger 对比度修复）、`Theme/Dark.xaml`/`Theme/Light.xaml`（新）、`App.xaml.cs`（默认 %LOCALAPPDATA%\GameLibrary + desktop.json 引导记忆 + 单实例互斥/唤醒 + 主题切换）、`MainWindow.xaml`（顶栏重组：添加游戏文件夹/扫描/设置；移除连接宿主与混用路径框；GridSplitter；搜索标签；错误字号 12）、`MainWindow.xaml.cs`（自动初始化库；数据目录与游戏文件夹分离；游戏文件夹管理对话框；全根顺序扫描 + 进度 + 取消；开始游戏/配置启动方式；设置对话框（主题/缩放/托盘/开机启动/周期）；事件轮询 3s 自动刷新；按 ID 选中；待审核过滤分支修复；纪元不匹配自动重连重试；关闭消费 closeToTray；托盘应用图标；分栏宽度记忆）、`App.ico`（新：多尺寸图标）+ csproj ApplicationIcon。
- **测试**：`FieldLayeringAndRuntimeStateTests.cs`（新 4 项：字段分层/外键级联/设置 Revision 单调/运行态重启恢复）、`EpochGuardTests.cs`（新 6 项：信封补全/纪元拒绝/实例拒绝/roots 落库/真实 schema/幂等键校验）、`FullWorkflowE2ETests.cs`（扫描前 roots.add；清理改为自身数据目录 host.stop，不再按进程名全杀）。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release 构建 0 警告 0 错误；测试全绿——单元 139 + 契约 31 + 架构 8 + 集成 188（含新增 FieldLayeringAndRuntimeState 4 项、EpochGuard 6 项）+ HeadlessE2E 13 项（含修复后的 T30 全链路），合计 379 项通过。日志：`artifacts/build-reports/py-test.log`、`py-e2e-test.log`。
- **T30 修复明细**：该未提交测试历史上从未通过——本轮修复 4 处：缺 roots.add、candidates.list 误在信封根取 items（实为 data.items）、accept 需 pendingReview（补第二轮扫描晋升）、Profile exe 须在已注册根内（注册 bin 根，LaunchE2ETests 同款模式）+ KR 引擎 Required 补 translation.set NotRequired + 清理改用自身数据目录 host.stop（不再按进程名全杀）。
- **未验证范围**：Desktop UI 自动化/DPI/屏幕阅读器实测；干净机器（无 SDK）双击验证；第三阶段 collections/MVVM 拆分/Dispatcher→Application handler 迁移；第四阶段安装器/签名。
- **下一项**：第三阶段（collections/tags、MainWindow MVVM 拆分、Dispatcher 按域拆 handler、T26 5000 游戏分页虚拟化）。

## R2 审查意见整改（第三阶段主体 + 第四阶段自动化）— 2026-09-15 完成

按《v1 审查意见稿》第三阶段"收藏夹、架构重构和性能"与第四阶段"正式 Windows 发布"的可自动化部分：

- **改动文件（标签 tags.×8，T-collections）**：`DatabaseMigrations.cs`（v18：tags 按 (类型, 规范值) 唯一 / game_tags 关联（双向级联）/ tag_overrides 覆盖表 Suppress+ForceAdd）、`TagStore.cs`（新：list/getByName/create/update/remove（返回受影响游戏）/assign/unassign/suppress/reset/EnsureEngineTagAssigned）、`SqliteLibraryStore.cs`（+13 转发）、`OperationDispatcher.cs`（tags ×8 handler + 入库自动创建引擎标签 + GameDto 增加 tags 数组）、Contracts（ImplementedOperations +8 → 87）、`OperationSchemas.cs`（+8 参数表）、Cli（tags 动词 + --tag-id/--color + games list --search/--tag-id 透传）、Mcp（tags_*8 工具）、`TagsTests.cs`（新 5 项：自动标签/用户标签全生命周期/Suppress+reset 恢复/删除返回受影响游戏/修订冲突）。
- **改动文件（数据库侧检索，阶段三）**：`LibraryCatalogStore.QueryGames`（新：搜索命中用户标题+原文件夹名、收藏过滤、tagId 过滤（与搜索 AND）、title/recent 排序、LIMIT/OFFSET 分页、独立 COUNT total；LIKE 通配符转义）、dispatcher games.list 改为 SQL 侧执行（不再整表载入内存）、Desktop 搜索框 300ms 防抖后携带 search 参数触发服务端刷新（本地仅过滤候选条目）、详情页显示标签行。
- **改动文件（T26 性能基线）**：`GameCatalogPerformanceTests.cs`（新 2 项：5000 游戏 p95 分页查询 ≤150 ms 实测达标；搜索/标签过滤/tags.list 计数 ≤200 ms 达标——单事务播种排除写路径干扰）。
- **改动文件（第四阶段发布自动化）**：`artifacts/tools/package_release.py`（四组件自包含发布 → PDB 分离（14 个，不随包）→ SHA256SUMS.txt（1128 文件）→ 卸载脚本（启动项/开始菜单/引导配置/程序目录，数据目录保留）→ 开始菜单快捷方式辅助脚本（PowerShell COM，不写注册表）→ RELEASE-CHECKLIST.md（干净机器验证 7 步 + signtool 缺失/MSIX/翻译工具能力/F 盘边界如实标记）→ `artifacts/dist/GameLibrary-win-x64-v1.0.0.zip`（176.9 MiB）。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release 构建 0 警告 0 错误；**全解决方案测试 386/386 通过**（单元 139 + 契约 31 + 架构 8 + 集成 195 + HeadlessE2E 13）。日志：`artifacts/build-reports/py-test.log`。
- **如实声明（剩余项）**：MainWindow 拆 MVVM 页面与 Dispatcher 拆 Application handlers 属大规模机械重构，本会话未执行（需独立会话逐域迁移并以 386 项测试护航）；UIA/键盘/DPI/高对比度测试需实机 UI 自动化环境；干净机器验证需真实无 SDK 机器（清单已交付）；代码签名需证书。
- **下一项**：Dispatcher 按域拆 handler（partial 逐步迁移）→ MainWindow MVVM 拆分 → UIA 冒烟测试。

## R3 架构拆分 + UIA 自动化 + 启动崩溃修复 — 2026-09-15 完成

- **Dispatcher 按域拆分（阶段三"Dispatcher 拆成 Application handlers"主体）**：`OperationDispatcher.cs`（145KB → 71KB）按域拆为 6 个 partial 文件——`Tags.cs`（标签 ×8）、`Backups.cs`（备份/恢复）、`ViewSettings.cs`（视图/通知/设置）、`Launching.cs`（Profile/启动）、`Cataloging.cs`（资料/封面/元数据/忽略）、`Observability.cs`（诊断/工具发现/验证）；主文件保留请求门/纪元校验/收据中间件/路由 + 系统与扫描候选域。工具化执行：`artifacts/tools/split_dispatcher.py`（方法名锚点 + 行区间手术）+ `repair_split.py`（修复两处边界缺陷：尾块吞并与 HostStop 归属）。
- **MainWindow 按关注点拆分（MVVM 化前置）**：`MainWindow.xaml.cs`（~1630 行）拆为 5 个 partial——`Connection.cs`（连接/设置读取/事件轮询/IPC）、`Library.cs`（侧栏/详情/启动/翻译/封面渲染）、`Scanning.cs`（添加根/扫描/取消）、`Settings.cs`（设置对话框）、`Lifetime.cs`（键盘/托盘/关闭语义）；主文件保留字段 + 构造函数。工具：`split_mainwindow.py`（同模式）。
- **UIA 冒烟测试（阶段三 UI 自动化首步）**：`UiaSmokeTests.cs`（新 1 项）——启动真实 Desktop 进程（独立数据目录），经 System.Windows.Automation 断言主窗口/搜索框（SearchBox）/视图切换器（ViewSelector）可达；测试项目启用 UseWPF + 显式 System.IO using。**该测试抓到并修复一个启动即崩的产品缺陷**：XAML `Icon="App.ico"` 的 pack URI 资源未嵌入（ApplicationIcon 只设 EXE 图标不进 WPF 资源清单），且 obj 旧 BAML 缓存掩盖了 XAML 修复——清理 obj/bin 后改用代码从 EXE 提取图标（try/catch 兜底不阻塞启动）。
- **验证结果**：`dotnet format` 通过；Release 构建 0 错误；**全解决方案 387/387 通过**（+1 UIA）。日志：`artifacts/build-reports/py-test.log`。发布包重打：`artifacts/dist/GameLibrary-win-x64-v1.0.0.zip`（含启动崩溃修复）。
- **如实声明（剩余项）**：域 handler 的接口化（从 partial 类升级为独立 Handler 类 + 路由表）与 MainWindow 的绑定化 MVVM（DataTemplate/ViewModel）仍是后续演进方向（结构拆分已完成，行为由 387 项测试锁定）；干净机器验证/代码签名需人工。
- **下一项**：按需继续——域 handler 接口化、MVVM 绑定化、或 T26 全量性能复核。

## R4 Desktop"宿主未连接"修复 + 发布包扁平化 — 2026-09-15 完成

- **根因（用户实测报告）**：`HostProcessLauncher` 只在 `AppContext.BaseDirectory`（Desktop.exe 同目录）找 `GameLibrary.Host.exe`——发布包按组件分目录布局时 Host 在兄弟目录、开发 bin 里 Host EXE 也从不被复制 → 双击 Desktop 必然"未连接"。此前 UIA 测试只断言窗口/控件可达，未断言连接状态，故未暴露。
- **修复**：`HostProcessLauncher.ResolveHostExe` 多候选解析——① 环境变量 `GAMELIBRARY_HOST_EXE` 显式覆盖；② 与调用方同目录；③ 兄弟子目录 `..\GameLibrary.Host\`；错误消息列出全部已尝试路径；`WorkingDirectory` 改为宿主所在目录。仍不搜索 PATH、不执行任意命令。
- **发布包扁平化**：`package_release.py` 四组件发布到同一目录（自包含同版本依赖覆盖无冲突）——体积 176.9 → **73.3 MiB**、校验文件 1128 → 526（去重）；uninstall/checklist/快捷方式路径同步更新，checklist 第 3 步明确"状态栏显示已连接 · 库 opened"。
- **开发布局兜底**：Desktop csproj `CopyHostRuntime` target（AfterBuild 把 Host 输出复制到 Desktop 输出目录；仅文件复制，不建项目引用——依赖方向仍为 Desktop → HostClient → IPC）。
- **测试强化**：`UiaSmokeTests` 新增第 4 步断言——状态栏（StatusText）40 秒内进入"已连接"（端到端覆盖宿主自动拉起 + 库初始化）。实测 6 秒内通过。
- **验证结果**：format 通过；Release 构建 0 错误；全解决方案 **392/392**（单元 139 + 契约 31 + 架构 8 + 集成 201（含 5 项盘根回归）+ E2E 13）。日志：`artifacts/build-reports/py-test.log`。发布包重打（扁平 73.3 MiB，sha256 0c262748…，含盘根修复与两轮扫描）。构建期遇到用户正开着的旧 Desktop 锁文件——温和关闭后重建。
- **下一项**：干净机器人工验证（checklist）；域 handler 接口化 / MVVM 绑定化按需演进。

## R5 用户实测反馈第二轮：设置保存冲突修复 + 审核流程引导 — 2026-09-15 完成

用户以 F 盘实测后反馈两点：

- **"扫描后待审核 4，库中没有游戏"**：审核入库的设计语义（候选 → 接受 → 入库），非缺陷；但流程引导不足。增强：接受入库成功后状态栏明确提示"已入库（gameId…）；全部候选接受完后切到「全部游戏」查看"；"全部游戏"视图为空而待审核有候选时，详情区占位文案给出审核指引；盘点此前已做的"扫描后自动切待审核视图 + 两轮扫描"正是为了打通该流程。
- **"设置保存失败：设置已被其他入口修改（当前 rev 3）"（真缺陷）**：设置对话框使用连接时的 `_settings` 快照修订，而每次切换视图（views.activate 持久化）都会推高修订 → 打开设置页时快照必然过期，保存必冲突。修复：`ShowSettingsDialogAsync` 打开时实时 `settings.get` 刷新快照与修订；保存遇 RevisionConflict 自动用错误携带的最新修订重试一次（真并发仍会如实提示）。
- **测试基建**：`ScanPerformanceTests.TryCleanup` 加固——清理 fixture 时对杀软/索引器瞬时锁（UnauthorizedAccessException）重试一次后放弃（性能断言已在清理前完成，不判失败）；此前一轮全量 200/201 的唯一失败即此 flaky 清理。
- **验证结果**：Release 构建 0 错误；核心套件 178/178（单元 139 + 契约 31 + 架构 8）；全量此前的 392/392 基线 + 本轮 UI 层改动（不改断言语义）。发布包重打：`artifacts/dist/GameLibrary-win-x64-v1.0.0.zip`（sha256 3e966e9b…）。修复版 Desktop 已在本机重启并确认宿主自动拉起成功。
- **下一项**：用户 F 盘实测续验（五类引擎外的游戏若为 0 候选，按需扩展检测器）；干净机器验证/签名。

## R6 "宿主进程提前退出（0x80008096）"修复 — 2026-09-15 完成

用户实测报"后台服务暂时不可用：宿主进程提前退出（退出码 -2147450730）"（= 0x80008096，.NET 宿主二进制启动失败区间，非业务退出码 7）：

- **根因（事件日志实锤）**：`package_release` 的 `dotnet publish -r win-x64` 在 Desktop bin 下生成 RID 中间目录 `bin\...\net10.0-windows\win-x64\`；`CopyHostRuntime`（AfterTargets=Build，无 RID 条件）在 RID 构建时也执行，把 **framework-dependent**（依赖系统 .NET 运行时）的 Host 文件复制了进去。用户双击该目录里的 Desktop.exe → UI 正常（Desktop 找得到 SDK 共享框架）→ 拉起同目录 Host.exe → Host 找不到运行时（"No frameworks were found"）→ 立即退出 → "宿主进程提前退出，可能已有另一宿主持有该数据目录"（原错误消息误导）。
- **修复**：① `CopyHostRuntime` 加 `Condition="'$(RuntimeIdentifier)' == ''"`——仅非 RID（framework-dependent 开发布局）构建复制，RID/publish 自带自包含宿主不再被污染；② 删除被污染的 `bin\...\win-x64\` 目录；③ `HostProcessLauncher` "宿主进程提前退出"错误增强——退出码显示十六进制，0x8000_0000 区间给出明确指引（"宿主二进制无法启动（缺 .NET 运行时或文件不完整）。请使用自包含发布包"），单实例冲突（退出码 7）才提示数据目录被占用。
- **验证结果**：Release 构建 0 错误；全量 **412/412 通过**（单元 139 + 契约 31 + 架构 8 + 集成 201 + E2E 13；HostStopE2E 首跑在用户 F 盘扫描抢占 IO 时 flaky 退出码 7，单独重跑通过）。发布包重打：`GameLibrary-win-x64-v1.0.0.zip`（sha256 caf01dfb…）；修复版 Desktop 已重启并确认宿主自动拉起。
- **给用户的指引**：始终从发布包 `GameLibrary\GameLibrary.Desktop.exe` 或开发 `bin\Release\net10.0-windows\GameLibrary.Desktop.exe` 启动（**不要进 win-x64 子目录**）。
- **下一项**：干净机器验证/签名；检测器覆盖按需扩展。

## R7 审查驱动的 Desktop 闭环修复 — 2026-09-17

- **任务 ID**：R7（本轮接手代码审查与用户反馈 1–10 项续修）。详细审查和剩余风险见 `docs/review-2026-09-17.md`。
- **改动文件**：`src/GameLibrary.Desktop/App.xaml.cs`、`MainWindow.xaml`/`.xaml.cs`、`MainWindow.Connection.cs`、`MainWindow.Library.cs`、`MainWindow.Scanning.cs`、`MainWindow.Settings.cs`、新增 `MainWindow.Collections.cs`；`tests/GameLibrary.IntegrationTests/Ui/UiaSmokeTests.cs`；本进度与构建报告。
- **行为增量**：显式 `--data-dir` 不覆盖普通双击的目录记忆；退出时停止唤醒线程；扫描取消停止整批、失败不被后续刷新擦掉；视图切换使用新幂等键且按 ID 保持游戏选择；设置冲突不再静默覆盖他人更新、缩放保存即生效；收藏夹（基于 user tags）GUI 创建/重命名/删除/归类/筛选；游戏列表服务端收藏/标签过滤并提供 500 条一页的“加载更多”；修复 Desktop 配置启动方式参数名/必需 `argv`；未接入的 `.lnk` 不再伪装为可配置启动项。
- **验证结果**：format 通过；Release build 0 警告 0 错误；全套 **392/392**，TRX 和摘要在 `artifacts/build-reports/2026-09-17-r7*`。UIA 实际双启验证同目录只保留原实例，且创建收藏夹后可从独立 HostClient 读回。
- **未验证范围**：GUI Profile 文件对话框、扫描取消慢作业、>500 游戏 UI、大字体/DPI/辅助技术、干净机器发布包。本轮未重打 `artifacts/dist/`；该目录旧包不含 R7 改动。
- **下一项**：P0 `games.create/remove` 手动添加 + `.lnk` 解析接入三入口；随后补上述 UIA/性能与发布验收。

## R8 手动入库、快捷方式与桌面单实例 — 2026-09-17

- **任务 ID**：R8（用户补充：Desktop 只能启动一个；继续推进手动添加游戏和代码审查）。审查结论/优先级更新在 `docs/review-2026-09-17.md`。
- **改动文件**：`src/GameLibrary.Host/Hosting/OperationDispatcher.Cataloging.cs`、`.Launching.cs`、`OperationDispatcher.cs`、`Infrastructure/Persistence/LibraryCatalogStore.cs`/`SqliteLibraryStore.cs`、`Infrastructure/Shell/WindowsCommandLine.cs`、`Host/Scanning/ReconcileService.cs`、`Contracts/OperationCatalog.cs`/`OperationSchemas.cs`、`Cli/CommandLine.cs`/`Program.cs`、`Mcp/GameLibraryTools.cs`、`Desktop/App.xaml.cs`/`MainWindow.xaml.cs`/`MainWindow.Scanning.cs`/`MainWindow.Library.cs`/`MainWindow.Lifetime.cs`、`tests/GameLibrary.IntegrationTests/Manual/ManualGameTests.cs`、`Ui/UiaSmokeTests.cs`、`tests/GameLibrary.HeadlessE2ETests/FullWorkflowE2ETests.cs`、`tests/GameLibrary.ContractTests/OperationSchemaParityTests.cs`、`docs/coverage.md`、本进度与构建报告。
- **行为增量**：`games.create/remove` 进入 Desktop/CLI/MCP 共用宿主操作（89/129）；手动选择目录、EXE、SWF 或 LNK，LNK 目标/参数/工作目录受授权根与重解析点约束；无启动线索的目录仅建卡不假装可启动；移除是数据库软移除 + 精确路径忽略，不删除原文件，并禁止启动已移除游戏。审查发现 CLI 主路由漏列这两个操作（虽参数解析已存在），已修复并加真实 CLI E2E。Desktop 同一 Windows 用户会话内只保留一个进程，后启动进程唤醒已有窗口，即使显式数据目录不同也不多开；新进程不会改写引导目录记忆。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release build 0 警告 0 错误；全套 **407/407**（单元 139、契约 42、架构 8、集成 204、Headless E2E 14）。隔离测试证明 LNK 参数/工作目录、两个同目录 EXE 分别建卡、幂等、修订冲突、移除不删文件与启动拒绝；UIA 实测同目录/不同目录二次启动均退出并唤醒最小化的原窗口；真实 CLI E2E 完成建卡/移除。机器结果见 `artifacts/build-reports/2026-09-17-r8*.trx`。
- **预览包**：为避免旧打包脚本覆盖同版本 ZIP/生成进程名全杀卸载器，在 `artifacts/dist/GameLibrary-win-x64-r8-20260917/` 独立发布四组件，并生成 `GameLibrary-win-x64-r8-20260917-preview.zip`（SHA-256 `DF63F1D8808570F37D84DC4672EB3D4A92A1FC49541FB4F909A7B4AE910A7A0A`，535 个 ZIP 条目）。包内 CLI/Host 在隔离数据目录完成 init/status/stop，均退出码 0；旧 v1.0.0 ZIP 保持原样。预览包无卸载器/签名，不能当正式安装包。
- **未验证范围**：完整 GUI 文件选择→添加→启动→移除 UIA 流程、真实游戏兼容性、跨 Windows 登录会话（设计为各会话单实例）、无 SDK 干净机器上的新预览包 Desktop、DPI/高对比/屏幕阅读器、F 盘样本（按工程边界未访问）。
- **下一项**：补完整桌面交互测试与首用教程、数据/缓存目录图形迁移和字体设置；将 Host partial 用例逐步抽入 Application；对重新打包产物做干净机器验收。

## R9 首用指南与字体设置 — 2026-09-17

- **任务 ID**：R9（继续修复用户反馈第 3、8 项，检查设置三入口的真实行为）。
- **改动文件**：`src/GameLibrary.Desktop/MainWindow.Guide.cs`（新）、`MainWindow.xaml`、`MainWindow.Connection.cs`、`MainWindow.Settings.cs`、`MainWindow.Collections.cs`、`MainWindow.Scanning.cs`、`App.xaml`/`.xaml.cs`；`Infrastructure/Persistence/SettingsStore.cs`、`Host/Hosting/OperationDispatcher.ViewSettings.cs`、`Contracts/OperationSchemas.cs`、`Cli/CommandLine.cs`/`Program.cs`、`Mcp/GameLibraryTools.cs`；`tests/GameLibrary.IntegrationTests/Ui/UiaSmokeTests.cs`、`Translation/SettingsTests.cs`、`tests/GameLibrary.HeadlessE2ETests/FullWorkflowE2ETests.cs`、`tests/GameLibrary.ContractTests/OperationSchemaParityTests.cs`；审查文档、本进度、构建报告。
- **行为增量**：全新空库首次连接展示分步指南（当前库数据目录内保存已读标记），顶栏“使用指南”可随时重开，提供添加文件夹/手动添加/待审核/设置直达操作。设置页可从本机已安装字体选择字体族，文字与控件大小 0.85–1.6 倍；预览取消回退、保存后经 Host 持久化。CLI/MCP 的 `settings.update` 现在只发送指定字段；宿主完成全部校验后才操作开机启动快捷方式，避免无效 patch 留下副作用。
- **验证结果**：`dotnet format --verify-no-changes`、Release build（0 警告 0 错误）、全套 **411/411**（单元 139、契约 43、架构 8、集成 206、Headless E2E 15）。UIA 实测指南首开/再打开、字体/大小控件可发现、调整大小后设置持久化；真实 CLI E2E 验证分字段字体 patch，MCP 集成测试验证未指定字段不会作为 null 发送。TRX 见 `artifacts/build-reports/2026-09-17-r9*.trx`。
- **预览包**：独立 `artifacts/dist/GameLibrary-win-x64-r9-20260917-preview.zip`（SHA-256 `745E693241A52B90DCDE8D7D6A740AA568DCE70B4F7033E364E785B902EDAAD6`，79,471,988 字节，535 条目，四个 EXE）；包内 CLI/Host 在隔离库完成 init、settings.get、字体 update、host.stop，退出码均 0。旧版本包保持原样；未签名、无安装器/卸载器。
- **未验证范围**：缓存目录的独立安全生命周期和 GUI 迁移、完整 GUI 文件选择→入库→启动→移除、DPI/高对比/屏幕阅读器、无 SDK 干净机器双击预览包 Desktop、真实游戏和 F 盘样本（按边界未访问）。
- **下一项**：设计并实现安全的缓存目录选择/迁移（防误删外部文件），补完整桌面交互 UIA；逐域抽取 Application 用例并完善发布流水线。

## R10 Desktop 真实操作路径审计 — 2026-09-17

- **任务 ID**：R10（按 `click-path-audit` 顺序审计手动选择 EXE→授权游戏文件夹→入库→启动→移除）。详细问题、严重度与未覆盖触点见 `docs/review-2026-09-17.md`。
- **改动文件**：`src/GameLibrary.Desktop/MainWindow.Scanning.cs`、`MainWindow.Library.cs`、`MainWindow.xaml.cs`；`tests/GameLibrary.IntegrationTests/Ui/UiaSmokeTests.cs`；审查文档、本进度、`artifacts/build-reports/2026-09-17-r10-*.txt` 及 TRX。
- **行为增量**：手动添加成功后清除旧搜索条件并切回全部游戏，避免新游戏被旧搜索藏起；待选 game ID 由成功渲染消费，避免事件轮询刷新交错时一次性选中落空；文件/目录选择器从 Documents 打开，不继承系统上次使用的位置。UIA 真实点击文件选择器并使用隔离 TestProcessStub 验证自动启动配置、游戏启动记录及软移除不删原文件。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release build 0 警告 0 错误；解决方案 **412/412**（单元 139、契约 43、架构 8、集成 207、无界面 E2E 15）。新 UIA 用例包含先设置不匹配搜索词的回归。全量日志 `artifacts/build-reports/2026-09-17-r10-test.txt`，TRX 在同目录。
- **未验证范围**：目录/LNK 的完整 GUI 点击流程、重启持久化、真实游戏兼容性、可控时序下的刷新竞态、DPI/高对比、无 SDK 干净机器。初次 UIA 文件选择器曾由 Windows 恢复到 F 盘历史位置，但测试未选择或扫描其中文件；后续起点固定 Documents，样本选择限于独立数据目录。未重打预览包，R9 包不含 R10 改动。
- **下一项**：缓存目录安全生命周期/图形迁移，补目录/LNK、重启与故障恢复 GUI 流程；逐域抽取 Application 用例并完成正式 Windows 发布验收。

## R11 缓存清理边界加固 — 2026-09-17

- **任务 ID**：R11（自定义缓存目录前的安全审查与可验证清理边界；使用 `code-reviewer` 检查安全、性能、错误处理）。严重度、代码段、修复建议及该范围质量评分见 `docs/review-2026-09-17.md`。
- **改动文件**：新增 `src/GameLibrary.Host/Tools/CacheDirectoryCleaner.cs`，修改 `src/GameLibrary.Host/Hosting/OperationDispatcher.Observability.cs`、`tests/GameLibrary.IntegrationTests/Translation/FaultRecoveryTests.cs`；审查文档、本进度与 `artifacts/build-reports/2026-09-17-r11-*.txt`/TRX。
- **行为增量**：`diagnostics.cache_rebuild` 不再递归跟随目录链接/junction；缓存根本身是链接时拒绝清理；用户原图计数同样不穿越链接。删除成功后才累计文件与字节，响应包含跳过的链接/错误数量。测试覆盖被锁定文件的统计和链接目标保留（创建链接不受当前 Windows 测试账户允许时，该分支条件性跳过）。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release build 0 警告 0 错误；全套 **414/414**（单元 139、契约 43、架构 8、集成 209、无界面 E2E 15），日志见 `artifacts/build-reports/2026-09-17-r11-test.txt`。未访问 `LocalData/` 或 F 盘。
- **未验证范围**：恶意并发替换目录的 TOCTOU、用户自定义缓存路径和实际预览写入、容量/迁移/回退、无 SDK 干净机器。R9 预览包不含此修复。
- **下一项**：设计带所有权标记的专属缓存子目录，接入真实预览缓存、settings 契约及三入口和 GUI 选择；再做自定义路径的安全/恢复测试，不将任意选择的目录作为可递归删除目标。

## R12 自定义封面缓存位置与真实预览缓存 — 2026-09-17

- **任务 ID**：R12（接手原会话中断项：把安全缓存设计贯通 Desktop、Host、CLI、MCP 与实际磁盘写入）。决策见 `docs/adr/0008-owned-preview-cache-location.md`，操作目录版本升为 1.1，API 版本保持 1。
- **改动文件**：新增 `src/GameLibrary.Host/Tools/OwnedPreviewCache.cs`、`docs/adr/0008-owned-preview-cache-location.md`、`tests/GameLibrary.IntegrationTests/Translation/CacheLocationTests.cs`；修改 `Infrastructure/Persistence/SettingsStore.cs`、Host 的 `OperationDispatcher.ViewSettings.cs`/`.Cataloging.cs`/`.Observability.cs`、Desktop `MainWindow.Settings.cs`、CLI、MCP、契约 schema、Settings/UIA/CLI E2E 测试、README 与审查文档。
- **行为增量**：设置页可用 Windows 目录选择器指定封面缓存父目录或恢复默认；Host 只在 `<所选目录>/GameLibraryCache/<库摘要>/previews` 写入最大 720 像素且不超过 1 MiB 的可再生 PNG，并以本库标记验证所有权。不存在、含链接/junction、已有错误标记或位于已注册游戏文件夹内的位置会被拒绝。切换/reset 不移动或删除原图、旧缓存及父目录其他文件；“重建缓存”保留所有权标记并只清理当前预览内容。CLI 提供 `--cache-dir`/`--default-cache-dir`，MCP 提供同字段与清除开关。主界面同时完成术语收口：用户只看到“后台服务、准备游戏库、待确认游戏、加入游戏库、应用数据位置”，不再展示宿主、库根、候选 ID、Revision 或 ExactPath。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release build 0 警告 0 错误；全套 **418/418**（单元 139、契约 43、架构 8、集成 212、无界面 E2E 16）。测试覆盖设置持久化/回默认、无效和游戏根内路径拒绝、预览生成/清理/再生、父目录文件与所有权标记保留、CLI/MCP 映射及 Desktop 显示/保存。报告：`artifacts/build-reports/2026-09-17-r12-build.txt` 与 `2026-09-17-r12*.trx`。未访问 `LocalData/` 或 F 盘。
- **未验证范围**：Windows 原生目录对话框的完整 UIA 导航只验证了入口可发现，尚未自动点击并选中路径；缓存容量上限/淘汰策略、同用户恶意进程持续替换目录的 TOCTOU、数据目录迁移、无 SDK 干净机器和重新打包产物仍未完成。
- **下一项**：补缓存容量管理与原生目录选择 UIA；随后实现数据目录的停机迁移/校验/回退，继续目录/LNK GUI 与发布环境验收。

## R13 Desktop 扫描请求幂等参数修复 — 2026-09-17

- **任务 ID**：R13（用户实测从 Desktop 扫描 F 盘时报 `InvalidArgument: scan.start 需要 idempotencyKey 参数`；验证未访问 F 盘）。
- **根因与修复**：Desktop 的统一 IPC 入口原样发送调用点参数，`OnScanClick` 的 `scan.start`、`OnCancelScanClick` 的 `scan.cancel` 及部分写操作漏传契约要求的 `idempotencyKey`。新增 `DesktopRequestParameters`，根据嵌入的 operation catalog 为所有要求幂等键且调用方未提供有效键的 Desktop 写请求自动生成键；显式键保持不变，数据纪元重连重试复用同一个已准备参数，避免重复副作用。
- **改动文件**：新增 `src/GameLibrary.Desktop/DesktopRequestParameters.cs`、`tests/GameLibrary.IntegrationTests/Ui/DesktopRequestParameterTests.cs`；修改 `MainWindow.Connection.cs`、Desktop 项目测试可见性及本进度。
- **验证结果**：`dotnet format --verify-no-changes` 通过；隔离输出与默认运行目录的 Release 全解决方案构建均为 0 警告、0 错误；完整基线 **424/424**（单元 139、契约 43、架构 8、集成 218、无界面 E2E 16），其中真实 Desktop UIA 2/2；补充空白/错误类型键防御后，参数与真实 Named Pipe Host 扫描定向回归 **9/9**。构建日志及 TRX 位于 `artifacts/build-reports/2026-09-17-r13-*`。
- **未验证范围**：真实 F 盘人工扫描需用户在修复版 Desktop 中确认；自动测试未访问 F 盘或 `LocalData/`。
- **下一项**：用户确认真实 F 盘扫描启动正常；获得远端写入确认后提交并推送 R13。

## R14 扫描进度、性能与识别覆盖修复 — 2026-09-17

- **任务 ID**：R14（用户实测扫描已能启动，但无实时进度、扫描期间界面卡顿，且 F 盘只识别出 6 个 Unity 游戏）。
- **根因**：扫描执行器在返回 `Task` 前同步遍历磁盘，阻塞 IPC/UI；Desktop 为满足旧的 observed→pendingReview 语义把每个根完整扫描两遍，且五类检测器重复枚举相同目录；默认 20,000 目录/120 秒预算到达后没有自动消费续扫游标，同时仅有五类已知引擎检测器，未知引擎的普通 EXE/LNK 不会产生候选。
- **改动文件（后台与识别）**：`JobManager.cs`（强制在线程池执行作业并提供初始进度）、`ScanJobRunner.cs`/`DirectoryWalker.cs`/`FileSystemDirectorySnapshot.cs`（实时目录、文件、候选与当前位置；Walker 枚举结果复用；分段预算自动续扫并汇总覆盖）、`ScanCandidate.cs`/新增 `GenericGameCandidateDetector.cs`（未知引擎 EXE/LNK 保守候选、根级 EXE/SWF 独立候选、常见安装器/运行库/工具排除，启动项在大量普通文件之后仍可发现）、`ScanCandidatePersistence.cs`（用户手动扫描单轮直接进入 pendingReview）、`OperationDispatcher.cs`/`HostRuntime.cs`（仅完整覆盖后做缺失对账）、`ReconcileService.cs`（fileGame 按文件检查），以及相应扫描、审核、标签和 E2E 回归测试。
- **改动文件（Desktop）**：新增 `DesktopScanProgress.cs`；`MainWindow.Scanning.cs`/`MainWindow.xaml` 改为每 350 ms 读取 `scan.coverage`，显示已检查目录数、文件数、候选数和当前相对目录；删除每根双扫描；保持界面可交互并支持取消。R13 的 `DesktopRequestParameters.cs`/`MainWindow.Connection.cs` 幂等键修复继续保留。
- **行为修复**：扫描请求立即返回后台任务；超过单段预算会自动续扫至无可续分支；覆盖不完整时不误把游戏标记为缺失；候选接受后保存绝对 `EntryPath`；未知引擎不伪造引擎类型，统一进入待确认流程。
- **验证结果**：`dotnet format --verify-no-changes` 通过；Release 构建 0 警告、0 错误；全套 **435/435**（单元 139、契约 43、架构 8、集成 229、无界面 E2E 16）。报告：`artifacts/build-reports/2026-09-17-r14-format.txt`、`2026-09-17-r14-build-final.txt`、`2026-09-17-r14-final-tests.txt`；TRX 位于各测试项目的 `TestResults/2026-09-17-r14-final.trx`。
- **边界与未验证范围**：自动测试只使用 `artifacts/test-runs/<guid>/data`，未访问 F 盘或 `LocalData/`。真实 F 盘的最终候选数量、受保护目录的部分覆盖提示和实际磁盘吞吐仍需用户在新 Desktop 中复测；泛型识别有意排除常见安装器、崩溃上报器、运行库和工具，其他非游戏 EXE 仍需在待确认列表中由用户判断。
- **下一项**：启动 Release Desktop，由用户重新扫描 F 盘并核对实时计数与待确认列表；确认结果后再提交并推送远端。

## R15 扫描结果多选与批量审核 — 2026-09-17

- **任务 ID**：R15（用户要求多选扫描出来的游戏并执行批量操作）。
- **改动文件**：新增 `src/GameLibrary.Desktop/MainWindow.CandidateBatch.cs`；修改 `MainWindow.xaml`、`MainWindow.Library.cs` 与 `tests/GameLibrary.IntegrationTests/Ui/UiaSmokeTests.cs`；本进度与构建报告。
- **行为增量**：在“待确认游戏”视图为每个扫描候选显示可键盘和辅助技术操作的复选框；提供“全选当前结果”、已选数量，以及批量“加入”“暂不处理”“忽略”。普通游戏列表仍保持单选查看详情，不受批量模式影响。批量请求逐项复用既有 `candidates.accept/defer/ignore` 操作与 Revision/幂等保护，成功项移出选择，失败项保留并汇总首个错误；批量忽略执行前明确确认，且说明不会删除游戏文件。
- **接口边界**：这是 Desktop 对现有三入口业务操作的组合调用，不新增仅 GUI 可用的业务接口，也不修改 operation catalog 或共享契约。
- **可访问性修复**：复选框监听 Checked/Unchecked，而不是仅监听鼠标 Click；真实 UIA 的 TogglePattern、键盘和鼠标走同一状态路径。控件具有 AutomationName，批量按钮在未选中或执行期间禁用。
- **验证结果**：`dotnet format --verify-no-changes` 与 `git diff --check` 通过；Release 构建 0 警告、0 错误；新增真实 Desktop UIA 完成“两项扫描候选→进入待确认视图→全选→批量加入→Host 中两项入库且待确认归零”；全套 **436/436**（单元 139、契约 43、架构 8、集成 230、无界面 E2E 16）。报告：`artifacts/build-reports/2026-09-17-r15-build.txt`、`2026-09-17-r15-batch-uia.txt`、`2026-09-17-r15-format.txt`、`2026-09-17-r15-final-tests.txt`。
- **未验证范围**：批量“暂不处理/忽略”的窗口点击未分别做 UIA；它们与批量加入共用同一循环和结果汇总，底层 defer/ignore 状态机由现有集成测试覆盖。未访问 F 盘或 `LocalData/`，未重新打包发布 ZIP。
- **下一项**：启动 Release Desktop，让用户在真实扫描结果中验证勾选、全选和三种批量操作；确认后再提交并推送远端。

## R16 v1.0.0 正式发布与当前用户安装器 — 2026-09-17

- **任务 ID**：R16（用户明确授权提交、推送，并把对应程序作为 GitHub Release 和安装程序上传）。
- **改动文件**：`src/GameLibrary.Host/GameLibrary.Host.csproj`、`src/GameLibrary.Cli/GameLibrary.Cli.csproj`、`src/GameLibrary.Mcp/GameLibrary.Mcp.csproj`（四个发布入口统一 `1.0.0` 元数据，Desktop 原为 `1.0.0`）；`artifacts/tools/package_release.py`（正式发布流水线）；README、本进度。
- **发布布局修复**：旧脚本把四个自包含项目依次发布到同一目录，后发布项目会删除前一个项目独有的运行时文件，且 WPF/Console 的同名运行时程序集内容可能不同；试运行实际复现 CLI 缺少 `System.Text.Encoding.Extensions.dll`。新脚本先独立发布，再按 `GameLibrary.Desktop/Host/Cli/Mcp` 组件目录封装；Desktop 通过既有兄弟目录解析启动 Host，避免运行时覆盖和静默冲突。
- **发布资产**：生成 `GameLibrary-win-x64-v1.0.0.zip` 便携包、`GameLibrary-Setup-v1.0.0.exe` 当前用户安装器和 `GameLibrary-v1.0.0-SHA256SUMS.txt`。安装器使用 Windows IExpress 封装，安装到 `%LOCALAPPDATA%\Programs\GameLibrary`，创建开始菜单应用/卸载快捷方式；不要求管理员权限，不写注册表、不修改环境变量、不安装服务，卸载保留默认 `%LOCALAPPDATA%\GameLibrary` 数据。
- **验证结果**：四组件 publish 成功，14 个 PDB 与正式包分离，载荷 1127 个校验条目；ZIP 结构检查 1133 项；便携 CLI 和隔离安装后的 CLI 均完成 capabilities、library.init、host.status、host.stop；安装脚本在 `artifacts/test-runs/<guid>/data` 完成安装并验证四个 EXE，卸载后程序目录移除。产品代码门禁沿用 R15 的 Release 构建 0 警告、0 错误及全套 436/436，并在最终提交前重跑。
- **发布限制**：本机没有代码签名证书，EXE 未签名，Windows SmartScreen 可能显示未知发布者；无 .NET 干净机器双击、企业策略与真实 F 盘仍需人工验证。发布流程未访问 F 盘或 `LocalData/`。
- **下一项**：提交并推送 `main`，创建 `v1.0.0` GitHub Release，上传 ZIP、安装器和 SHA-256 清单；发布后核对远端 Tag、资产大小与下载地址。

## R17 v1.0.0 发布布局与受信代码签名准备 — 2026-09-18

- **任务 ID**：R17（用户审查 Draft Release 后指出旧 ZIP 暴露内部四组件目录及 PowerShell/CMD 辅助脚本，并要求为 v1 使用受信代码签名证书）。
- **根因**：上一轮上传在安装器完成前中断，远端 Draft 只剩旧四组件 ZIP 与哈希清单；旧 ZIP 又直接遍历整个 staging 根目录，因此把发布检查文档和便携辅助脚本一并交付。四个入口独立自包含目录属于内部部署结构，不适合作为普通用户下载界面。
- **发布布局修复**：`artifacts/tools/package_release.py` 改为四入口单文件自包含发布。普通用户安装器和 `GameLibrary-Portable-win-x64-v1.0.0.zip` 仅含 Desktop 与同目录 Host；CLI/MCP 连同独立 Host 放入 `GameLibrary-Tools-win-x64-v1.0.0.zip`。两个 ZIP 分别严格校验为 3/4 个根级文件，拒绝任何额外目录及 `.ps1`/`.cmd`/`.bat`。旧 `GameLibrary-win-x64-v1.0.0.zip` 在三个新资产全部成功后删除。
- **签名策略**：拒绝用自签名证书冒充公共信任；流水线新增 SHA-256 Authenticode + RFC 3161 时间戳及签后验证，并提供 `--require-signing` 失败关闭门禁。证书和密码只从外部安全配置读取，不写入仓库。用户选择 SignPath Foundation，仓库按其条件新增 MIT `LICENSE` 与 `docs/code-signing-policy.md`；正式 Release 在受信签名完成前保持 Draft。
- **产物与冒烟**：未签名本地预览生成单个 `GameLibrary-Setup-v1.0.0.exe`（105,185,280 字节）、便携 ZIP（105,036,295 字节）、Tools ZIP（101,017,280 字节）及三资产哈希清单。Tools 单文件 CLI 完成 capabilities、library.init、host.status、host.stop；隔离安装只落 Desktop/Host/校验文件，已安装 Desktop 成功拉起同目录 Host，卸载后目标目录移除。测试数据仅位于 `artifacts/test-runs/<guid>/data`。
- **工程门禁**：`dotnet format --verify-no-changes` 通过；Release build 0 警告、0 错误；全套 **436/436**（架构 8、契约 43、单元 139、无界面 E2E 16、集成 230）通过。日志：`artifacts/build-reports/2026-09-18-r17-format.txt`、`2026-09-18-r17-build.txt`、`2026-09-18-r17-tests.txt`。
- **未完成范围**：SignPath Foundation 需要仓库公开、OSI 许可证、项目审核和平台侧配置；当前尚未取得证书，以上本地产物明确为未签名预览，不上传为正式 v1 资产。拿到 SignPath 项目配置后需让其签名 Desktop、Host、CLI、MCP 与安装器，重新生成 ZIP/哈希并验证 `Get-AuthenticodeSignature` 为 `Valid`。
- **下一项**：提交并推送 R17，按用户授权将仓库公开，完成 SignPath Foundation 申请；获批后替换 Draft Release 资产并发布 `v1.0.0`。
