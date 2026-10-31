# GameLibrary 当前代码架构

## RG-04 / v1.8.2 草稿（2026-10-31）

JobManager 正常情况下只保留最近 128 个可淘汰终态对象，其余结果暂存到独占、关闭即删除的会话文件，按轻量索引回读；转存失败或终态记录待重试时保留对象。不是跨重启历史库，也没有增加 SQLite schema。封面缓存增加两个并发名额和按消费者取消的队列。已知小批游戏变更在成员、排序、库身份及纪元均安全时局部更新，其余完整重查。

本轮静态图谱包含 441 个文件、4,321 个节点、43,356 条关系；CALLS 直接解析 9,491 条、未解析 24,660 条。见 [RG-04 快照](rg04-footprint.json)与[实施及统一验收增量清单](../development/2026-10-31-rg04-implementation.md)。本轮未运行测试、构建或性能实测；以下 RG-03/02 数值保留为历史快照。

## RG-03 / v1.8.2 草稿（2026-10-31）

在单 Bridge 基础上增加前端可见行挂载、屏外封面引用释放、隐藏期间的刷新域合并和可选界面延迟加载。SettingsProvider、事件消费、库身份失效和游戏退出唤醒仍保留。Rust 通过内部布尔事件通知窗口可见性；不是用失焦代替隐藏。

HostClient 与 Bridge 采用内置 JSON 源生成元数据；Host 公共 IPC、权限、状态字符串及 SQLite schema 不变。Bridge 提供显式 NativeAOT 候选配置，默认发布仍为压缩自包含方式。该配置及前端改动尚未编译或运行验收，见 [实现与统一验收清单](../development/2026-10-31-rg03-implementation.md)。

本轮静态图谱包含 439 个文件、4,283 个节点、42,827 条关系；CALLS 中 9,395 条直接解析、24,457 条未解析。详细数值见 [RG-03 快照](rg03-footprint.json)。这是源文件结构索引，不代表类型检查、JSON 生成代码或运行正确性。

## RG-02 / v1.8.2（未发布）

Tauri Rust 壳改为一个 C# Bridge 进程。普通请求和 `events.wait` 各有一条独立 HostConnection；两条通道分别串行，可同时在途。私有 stdio 使用独立 `transportId` 配对乱序响应，不改变 Host 的 `requestId`、权限、库身份或 SQLite schema。传输失败拒绝前端 invoke，保留幂等键；业务 Envelope 保持原样。

2026-10-30 改以发布分支 `codex/release-v1.8.1` 的 `4f7da8a` 为基线，保留扫描边界、快捷方式去重及现有压缩发布修复。压缩收益已属于 v1.8.1，不重复计为本轮增量；本轮验收重点是单 Bridge 的并发与占用。验证记录见 [RG-02 记录](../development/2026-10-30-rg02-validation.md)。

本 worktree 已于 2026-10-30 增量同步结构图谱：432 个文件、4,240 个节点、42,109 条关系；其中 CALLS 有 9,336 条直接解析、24,103 条未解析。没有安装嵌入模型或宣称全部调用已解析，数值见 [本轮快照](rg02-footprint.json)。下面的 2026-10-27 社区统计及 `architecture-overview.json` 保留为历史记录。

## 历史图谱快照（2026-10-27）

2026-10-27，主程序 v1.8.0，宣传网站 v1.5.0。基础提交为 `316e3759b8c7203ae4c631150d96787c2fdab3db`（`main`），图谱已按当前工作区增量同步，插件返回 `status: ok`，无增量解析错误。

## 图谱快照

| 项目 | 当前值 |
| --- | ---: |
| 索引文件 | 425 |
| 节点 | 4,135 |
| 数据库关系 | 40,720 |
| 代码社区 | 28 |
| 社区间关联对 | 24 |

语言覆盖 C#、Rust、TypeScript、TSX、JavaScript、Python 和 PowerShell。社区结构来自插件；下面的模块名称、运行链路和项目引用方向已对照源码整理。

## 运行链路

```mermaid
flowchart TB
    subgraph DesktopClients[本地客户端]
        React[React 界面与 hooks] --> API[api.ts / Tauri invoke]
        API --> Rust[Tauri Rust 壳]
        Rust --> Bridge[C# TauriBridge 单进程 / transportId]
        Bridge --> Requests[普通请求连接]
        Bridge --> Events[events.wait 连接]
        WPF[WPF Desktop]
        CLI[CLI]
        MCP[MCP 服务]
    end
    Requests --> Client[HostClient / HostConnection]
    Events --> Client
    WPF --> Client
    CLI --> Client
    MCP --> Client
    Client --> Pipe[每库命名管道]
    Pipe --> Host[HostRuntime / OperationDispatcher]
    Host --> Gate[权限与会话租约 / 读快照 / 写入准入]
    Gate --> Services[Host 业务处理器与后台作业]
    Services --> App[Application 用例与端口]
    App -->|调用存储端口| Infra[Infrastructure 实现]
    Services --> Infra
    Infra --> SQLite[(SQLite 游戏库)]
    Infra --> Files[游戏文件 / 封面 / 备份]
    Website[独立 Next.js 宣传网站]
```

运行图中的箭头表示请求或数据流。`Application` 调用存储端口，`Infrastructure` 实现这些端口；编译依赖方向见下一张图。

Rust 壳按 `operationId == "events.wait"` 选择事件通道，其余操作进入普通请求通道；两条通道共用一个 Bridge 进程。Bridge 通过标准输入输出收发带 `transportId` 的 JSON，再由两条独立 `HostConnection` 连接命名管道。Host 持有游戏库连接，集中处理权限、会话和数据访问。

## C# 项目依赖

箭头严格对应各 `.csproj` 中的直接 `ProjectReference`，不展开传递引用。

```mermaid
flowchart LR
    Bridge[GameLibrary.TauriBridge] --> Client[GameLibrary.HostClient]
    Desktop[GameLibrary.Desktop] --> Client
    CLI[GameLibrary.Cli] --> Client
    MCP[GameLibrary.Mcp] --> Client
    Bridge --> Contracts[GameLibrary.Contracts]
    Desktop --> Contracts
    CLI --> Contracts
    MCP --> Contracts
    Client --> Contracts
    Host[GameLibrary.Host] --> Contracts
    Host --> Application[GameLibrary.Application]
    Host --> Infrastructure[GameLibrary.Infrastructure]
    Infrastructure --> Application
    Application --> Domain[GameLibrary.Domain]
```

`Domain` 和 `Contracts` 均无其他项目引用。`Application` 编排游戏目录、候选审核、备份创建和恢复用例；`Infrastructure` 提供 SQLite 与文件实现。Host 负责装配、IPC 分发以及扫描、启动、翻译等运行服务。

## 主要源码入口

以下路径相对于仓库根目录 `D:\Official\GameLibrary`。

| 模块 | 源文件或目录 |
| --- | --- |
| React 界面 | [App.tsx](../../src/GameLibrary.Tauri/src/App.tsx) |
| 前端共享状态 | [state.ts](../../src/GameLibrary.Tauri/src/lib/state.ts)、[hooks](../../src/GameLibrary.Tauri/src/lib/hooks/) |
| 前端请求封装 | [api.ts:118](../../src/GameLibrary.Tauri/src/lib/api.ts#L118) |
| Rust 请求与事件分流 | [lib.rs:232](../../src/GameLibrary.Tauri/src-tauri/src/lib.rs#L232) |
| C# Bridge | [BridgeServer.cs](../../src/GameLibrary.TauriBridge/BridgeServer.cs) |
| 命名管道客户端 | [HostConnection.cs:33](../../src/GameLibrary.HostClient/HostConnection.cs#L33) |
| Host 装配与生命周期 | [HostRuntime.cs](../../src/GameLibrary.Host/HostRuntime.cs) |
| 权限检查与请求分发 | [OperationDispatcher.cs:234](../../src/GameLibrary.Host/Hosting/OperationDispatcher.cs#L234) |
| 公共协议与权限配置 | [Contracts](../../src/GameLibrary.Contracts/)、[ClientPermissionProfiles.cs](../../src/GameLibrary.Contracts/ClientPermissionProfiles.cs) |
| 用例与端口 | [Application](../../src/GameLibrary.Application/) |
| 领域模型与引擎识别 | [Domain](../../src/GameLibrary.Domain/) |
| SQLite 连接与存储端口实现 | [SqliteLibraryStore.cs:16](../../src/GameLibrary.Infrastructure/Persistence/SqliteLibraryStore.cs#L16) |
| WPF / CLI / MCP 入口 | [Desktop](../../src/GameLibrary.Desktop/)、[Cli/Program.cs](../../src/GameLibrary.Cli/Program.cs)、[Mcp/Program.cs](../../src/GameLibrary.Mcp/Program.cs) |
| 独立宣传网站 | [MarketingPage.tsx](../../website/components/MarketingPage.tsx)、[GlassControls.tsx](../../website/components/GlassControls.tsx)、[app](../../website/app/) |

## 插件原生文件与复现命令

| 文件 | 绝对路径 | 内容 |
| --- | --- | --- |
| 交互架构图 | `D:\Official\GameLibrary\.code-review-graph\graph.html` | 插件原生 community 视图，可搜索、缩放、查看节点 |
| D3 运行文件 | `D:\Official\GameLibrary\.code-review-graph\d3.v7.min.js` | 与 HTML 一起保留，供本地图页使用 |
| JSON 导出 | `D:\Official\GameLibrary\.code-review-graph\graph.json` | 插件导出的节点、边、社区和执行流 |
| 图谱数据库 | `D:\Official\GameLibrary\.code-review-graph\graph.db` | 插件持久化索引；以上数据库统计来自此文件 |
| 架构 API 原始结果 | `D:\Official\GameLibrary\docs\code-review-graph\architecture-overview.json` | 28 个社区、24 对关联、耦合提示和提交元数据 |
| 本文与 Mermaid 源码 | `D:\Official\GameLibrary\docs\code-review-graph\architecture.md` | 可继续编辑的运行图、项目依赖图和源码入口 |

在仓库根目录执行：

```powershell
code-review-graph update --repo D:\Official\GameLibrary
code-review-graph visualize --repo D:\Official\GameLibrary --mode community --format html
code-review-graph visualize --repo D:\Official\GameLibrary --mode community --format json
```

`.code-review-graph` 下的导出和数据库由插件忽略，不提交到 Git；`docs/code-review-graph` 保存可跟踪的架构说明与 API 快照。

## 读图边界

数据库有 40,720 条关系；本次 JSON 的 `edges` 数组含 19,362 条边、`flows` 数组含 100 项。导出集合与数据库总数口径不同，应分别读取，不能把导出集合当成完整数据库。

32,270 条 `CALLS` 中，插件报告直接解析 9,062 条、未解析 23,208 条。跨语言 IPC、动态分发和同名方法仍需对照源码，图上的关联不能直接当作运行时调用事实。

插件给出 15 条高耦合提示，其中包括 Host 与集成测试、Infrastructure 与集成测试的关系；测试对生产代码的引用不单独构成重构理由。生产社区中 Host 与 Infrastructure 关联 212 条，可在后续重构审计中继续追踪；本次未据此修改架构。

本次完成索引同步、原生 HTML/JSON 导出与文档更新，未运行测试、构建、lint 或类型检查。SVG 导出因本地缺少 `matplotlib` 未完成。

早期实现记录仍见 [优化实施记录](optimization-implementation.md)、[名称翻译](../title-translation.md)、[Unity 加载器兼容](../development/v1.7.11-loader-compatibility-plan.md)和[网站实施记录](website.md)。
