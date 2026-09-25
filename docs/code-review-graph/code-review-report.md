# GameLibrary 代码审查报告

## 审查元数据

| 项目 | 值 |
|---|---|
| 工具 | `open-code-review` / `ocr v1.12.9` |
| Provider / Model | `deepseek` / `deepseek-flash` |
| 审查范围 | `489a6c9..84a99bc` |
| 选中审查文件 | 1 个 |
| 排除文件 | 3 个 Markdown（`unsupported_ext`） |
| OCR 会话 | `ad8387e3-f99c-47fd-b53a-3d1650f54cbc` |
| 结果 | 5 条：1 high、4 medium |

本次是基于 Git 差异的增量审查，不代表 263 个源码文件都被 OCR 重新扫描。范围内的 4 个新增文件中，OCR 只选择了 `.github/workflows/code-review-graph.yml`；`docs/code-review-graph/*.md` 因扩展名被排除。`.mimosa/`、`.qoder-credits/` 等本地未跟踪审计产物没有纳入审查。

## High

### H-01 第三方 GitHub Action 使用可变 tag

- **位置**：`.github/workflows/code-review-graph.yml:17`
- **类别**：security
- **问题**：`tirth8205/code-review-graph@v2.3.9` 是可移动 tag。上游 tag 被重指向或账号被接管后，PR 工作流会执行未经审查的新代码；该 Job 同时拥有 `pull-requests: write` 权限。
- **建议**：改为完整 40 位 commit SHA，并保留版本注释，例如 `tirth8205/code-review-graph@<full-sha> # v2.3.9`。升级 action 时走单独 PR 并重新审查。

## Medium

### M-01 工作流没有执行超时

- **位置**：`.github/workflows/code-review-graph.yml:13`
- **类别**：other
- **问题**：网络、Registry 或第三方 action 卡住时，Job 可能占用 GitHub runner 到默认上限。
- **建议**：在 Job 级别增加 `timeout-minutes`，按该图谱任务的实际耗时设置 10–15 分钟，并在超时后保留失败状态供排查。

### M-02 Pull Request 没有并发取消策略

- **位置**：`.github/workflows/code-review-graph.yml:3-4`
- **类别**：other
- **问题**：同一 PR 快速推送多次时，旧的图谱审查仍会继续运行，造成重复消耗和评论竞态。
- **建议**：增加基于 `${{ github.workflow }}-${{ github.ref }}` 的 `concurrency` group，并设置 `cancel-in-progress: true`。

### M-03 checkout 默认浅克隆可能缺少 PR 基线

- **位置**：`.github/workflows/code-review-graph.yml:15`
- **类别**：bug
- **问题**：`actions/checkout` 默认 `fetch-depth: 1`。如果 `code-review-graph` action 需要 merge-base、PR base 或提交历史来计算影响范围，浅克隆会让分析失败或不完整。
- **建议**：确认 action 文档的历史依赖；如果需要基线，设置 `fetch-depth: 0`，或至少 fetch PR base SHA。

### M-04 checkout 版本与仓库现有 CI 不一致

- **位置**：`.github/workflows/code-review-graph.yml:15`
- **类别**：maintainability
- **问题**：新增工作流使用 `actions/checkout@v7`，现有 `.github/workflows/ci.yml` 两处使用 `actions/checkout@v5`。若 `v7` 尚未发布或不可解析，工作流会在首步直接失败；即使可用，也增加了版本维护分叉。
- **建议**：先确认 `v7` 的确存在，再统一仓库版本；更稳妥的做法是统一使用经过验证的完整 commit SHA。

## 优先级

1. **立即处理 H-01**：固定第三方 action 的 commit SHA，并复核 `pull-requests: write` 是否确实需要。
2. **随后处理 M-01、M-02**：增加超时和并发取消，避免 runner 与 PR 评论资源浪费。
3. **在首次 PR 运行前处理 M-03、M-04**：确认历史深度和 `actions/checkout` 版本，确保工作流能稳定启动并拿到正确基线。

## 验证记录

- `ocr llm test`：连接成功。
- OCR：`Review complete: 5 finding(s) across 1 selected item(s)`。
- OCR 工具调用失败数：0。
- `code-review-graph` 已同步到 `84a99bc`，图谱状态为 `head_matches_build=true`。
- 本次未自动修改代码，也未执行构建或测试；审查目标是 CI 工作流差异。

## 全项目静态审计（code-review-graph + 源码复核）

### 覆盖范围与限制

本轮使用 `code-review-graph` 对当前 `main` 的完整源码树建立/刷新图谱，并对关键边界源码进行人工复核：

| 项目 | 结果 |
|---|---|
| 图谱基线 | `5aac0b20fba6cfaa13a6de305d4b9464e3fbefcf` |
| 源码文件 | 263 |
| 图节点 / 边 | 2,572 / 19,723 |
| 函数 / 类 / 测试节点 | 1,895 / 412 / 2 |
| 直接解析调用 / 未解析调用 | 4,341 / 11,800 |
| 执行流 | 212 |
| 社区 / 社区对 | 18 / 21 |
| 知识缺口 | 72（50 isolated、20 untested hotspots、1 thin community、1 single-file community） |

这代表图谱覆盖了整个项目的可解析文件，不代表每一行都完成了人工语义审查。11,800 个未解析调用和只有 2 个测试节点会降低自动影响分析和测试覆盖结论的置信度；涉及这些节点的结论需要补充运行时或端到端验证。

### High

#### H-02 IPC 权限由客户端自报，受限 agent 可自升权

- **位置**：`src/GameLibrary.Host/Ipc/PipeServer.cs:45-50,98-121,135-140`；`src/GameLibrary.Contracts/Ipc/WireMessages.cs:42-46`；`src/GameLibrary.Host/Hosting/OperationDispatcher.cs:308-320`；`contracts/operations.v1.json:135-138`
- **类别**：security
- **事实**：Named Pipe 使用 `PipeOptions.CurrentUserOnly`，因此边界是“同一 Windows 用户”；握手中的 `Permissions` 由客户端提供，Host 原样写入每个 `IpcRequest.GrantedPermissions`。`HasPermission` 将 `null` 视为不限权，并把客户端自报的 `access.admin` 视为全权。
- **风险**：任何以同一 Windows 用户运行的进程都可以连接该 pipe，并发送 `Permissions = null` 或包含 `access.admin` 的握手。这样可以绕过 catalog 中 `access.configure`/`access.revoke` 的 `access.admin` 限制。契约注释明确写有“受限 agent 不可自升权”，当前实现没有服务端身份与权限授予之间的可信绑定。
- **建议**：将权限授予移到 Host 侧：按客户端身份（PID/token、受控启动参数或签名 capability）映射权限；至少拒绝客户端自报 `access.admin`，并为受限 agent 使用不可伪造的一次性 capability。保留 `CurrentUserOnly` 作为操作系统账户边界，但不要把它当作 agent 身份认证。补充同用户恶意客户端的集成测试。
- **条件说明**：如果产品明确把同一 Windows 用户下的所有进程都视为完全可信，此项风险等级可下调为设计约束；这与当前 `access.*` 契约的“不同 agent 可被限制”语义不一致，需由产品威胁模型确认。

### Medium

#### M-05 IPC 客户端缺少空闲读取超时和连接数上限

- **位置**：`src/GameLibrary.Host/Ipc/PipeServer.cs:38-91,94-140`；`src/GameLibrary.Contracts/Ipc/IpcFrame.cs:23-47`
- **类别**：security / availability
- **事实**：每个连接都进入 `ServeClientAsync`，读取帧时只使用 Host 关闭令牌；单帧允许最大 8 MiB，读取过程没有每帧/每连接的空闲超时，也没有应用层并发客户端上限。
- **风险**：同一用户进程可以连接后只发送不完整帧，持续占用一个客户端任务；多个慢连接会消耗 Host 的连接处理能力。8 MiB 上限限制了单次分配，但没有解决 slowloris 式长期占用。
- **建议**：为握手和每个请求建立可配置的 idle timeout；限制同时服务的连接数，超限快速拒绝；记录超时和拒绝指标。保持 8 MiB 上限，并按消息类型设置更小的握手/控制请求上限。

#### M-06 高耦合和超大函数增加修改半径

- **图谱事实**：社区间最高耦合为 `persistence-try → persistence-async` 171 条调用、`hosting-handler → persistence-async` 132 条、`tools-detector → identity-game` 126 条、`hosting-handler → persistence-try` 114 条；桥接节点包括 `DispatchCore`、`ConnectAsync`、`ServeClientAsync`、`AcceptLoopAsync`。
- **超大函数**：`useLibraryController` 543 行（`src/GameLibrary.Tauri/src/lib/state.ts:171-713`）、`ScanHostOperationAsync` 530 行（`src/GameLibrary.Cli/Program.cs:138-667`）、`DetailSheet` 483 行（`src/GameLibrary.Tauri/src/components/DetailSheet.tsx:45-527`）、`App` 370 行（`src/GameLibrary.Tauri/src/App.tsx:25-394`）、`ShowSettingsDialogAsync` 340 行（`src/GameLibrary.Desktop/MainWindow.Settings.cs:26-365`）。
- **判断**：这些是可维护性和回归风险信号，不等同于单个功能缺陷。优先把 `DispatchCore`、IPC 连接生命周期和前端状态控制器拆成可独立测试的边界，再处理 UI 大组件。

#### M-07 测试图谱覆盖不足

- **图谱事实**：263 个文件仅识别到 2 个测试节点、16 条 `TESTED_BY` 边；20 个高连接度节点没有测试关联，其中包括 `OperationDispatcher.DispatchCore`、`IpcRequests.InvalidArgument`、Tauri `useLibraryController`、`DetailSheet`、CLI `ScanHostOperationAsync` 和 WPF `ShowSettingsDialogAsync`。
- **判断**：这会放大 IPC 权限、扫描、启动和恢复流程的回归风险。应先为 Host IPC 握手/权限、路径边界、备份恢复和 launch.execute 建立集成测试，再补前端状态控制器测试。

### 架构与流程结论

- 客户端入口为 Tauri + React、WPF、CLI、MCP，共用 `HostClient` 和按库命名的 Named Pipe；Host 通过 `OperationDispatcher` 分发到扫描、目录、备份、启动、设置等 handler，基础设施层负责 SQLite 与文件系统。
- 主要高关键度流程为 `PollEventsAsync`（0.8529）、`ConnectAsync`（0.8339）、Host `Main`（0.8074）、`RunAsync`（0.8000）、`LaunchExecute`（0.7870）和 `OnScanClick`（0.7833）。完整 Mermaid 图见 `docs/code-review-graph/architecture.md` 与 `docs/code-review-graph/flows.md`。
- `RootRegistry.Contains` 和 `BackupArchive.ResolveManifestPath` 已使用带分隔符的规范化边界检查；`LaunchRegistry` 使用 `ProcessStartInfo.ArgumentList`，并限制 EXE/SWF 和工作目录存在。本轮未在这些路径中确认新的路径穿越或参数拼接缺陷。

### 验证记录（全项目审计）

- `npm run typecheck`（`src/GameLibrary.Tauri`）：通过。
- `dotnet test GameLibrary.sln --no-restore`：未执行成功，环境没有 `global.json` 要求的 .NET SDK `10.0.401`；本机报告“Installed SDKs: No .NET SDKs were found”。这不是代码测试失败，属于验证环境缺失。
- `code-review-graph` 增量刷新后：`head_matches_build=true`，基线为 `5aac0b2`。
- 未修改业务源码；本次修改仅更新审查报告和图谱说明文档。

## 综合优先级

1. **P0 / 安全设计确认**：确认 H-02 是否违反产品威胁模型；若受限 agent 需要可信隔离，先改为 Host 侧授予权限并补集成测试。
2. **P1 / 可用性与资源保护**：为 M-05 增加 IPC idle timeout、连接上限和指标。
3. **P1 / CI 供应链**：处理 H-01，固定第三方 action SHA；随后补 M-01、M-02、M-03、M-04。
4. **P2 / 回归防护**：为 IPC、扫描、备份恢复、启动流程增加测试，优先覆盖图谱列出的未测试桥接节点。
5. **P2 / 可维护性**：按桥接节点和超大函数拆分边界，拆分前后都重新刷新图谱并复核受影响流程。
