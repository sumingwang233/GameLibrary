# 开发进度登记

按《开发Agent任务书》要求，每完成一项任务在此登记：任务 ID、改动文件、验证结果、未验证范围、下一项。

## T00 工程基线 — 2026-09-13 完成

- **环境**：安装 .NET SDK 10.0.401 至用户目录 `C:\Users\sumingwang\.dotnet-sdk-10.0`（系统原仅有运行时无 SDK；未修改环境变量，`global.json` 锁定版本）。
- **改动文件**：`global.json`、`NuGet.config`、`Directory.Build.props`、`Directory.Packages.props`、`.editorconfig`、`.gitignore`、`AGENTS.md`、`GameLibrary.slnx`、`src/` 9 个工程、`tests/` 6 个工程。
- **验证结果**：Release 构建 0 警告 0 错误；`dotnet format --verify-no-changes` 通过；12 项测试全部通过（架构依赖方向 8 + 信封契约 4）。报告：`artifacts/build-reports/2026-09-13-t00.md`。
- **未验证范围**：Desktop 窗口运行时交互；空测试工程无用例；未创建/连接任何数据目录。
- **下一项**：T20 架构契约/ADR。

## T20 架构契约/ADR — 2026-09-13 完成

- **改动文件**：`docs/adr/0001..0007`、`contracts/operations.v1.json`（29 命名空间 111 操作，全部 status=planned）、`contracts/README.md`（命名规则/治理/负责人）、`tests/GameLibrary.ContractTests/OperationCatalogTests.cs`。
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
