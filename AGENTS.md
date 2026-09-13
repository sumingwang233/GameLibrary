# AGENTS.md — GameLibrary 工程边界

适用范围：`D:\Official\GameLibrary` 及其子目录内的开发工作。先读 README.md 的约束优先级，再按主策划案、接口契约、补充规格执行；任务拆解见《开发Agent任务书.md》。

## 可写/不可写

- 可写：`src/`、`tests/`、`fixtures/generated/`（仅唯一 test-id 子目录）、`docs/`、`contracts/`、`artifacts/`。
- 不可写：现有策划文档（4 份 md）、`.obsidian/`、`LocalData/`（本机日常库，测试禁止连接）、`F:\` 全盘（只读扫描边界；开发阶段根本不访问）。
- 测试数据只能用 `artifacts/test-runs/<guid>/data`；不创建、不连接日常库。
- 不修改用户环境变量、注册表、系统 PATH；不安装 Windows 服务。

## 开发环境

- 本机未安装系统级 .NET SDK；使用用户目录 SDK：`C:\Users\sumingwang\.dotnet-sdk-10.0\dotnet.exe`（10.0.401，被 `global.json` 锁定）。bash 中先 `export PATH="$USERPROFILE/.dotnet-sdk-10.0:$PATH"`，仅对当前命令会话生效。
- 目标框架 `net10.0` / `net10.0-windows`；包版本集中在 `Directory.Packages.props`，禁止浮动版本。
- 每次交付门禁：`dotnet format --verify-no-changes`、Release `dotnet build`、相关 `dotnet test` 通过；输出保存到 `artifacts/build-reports/`。
- 每完成一个任务，在 `docs/progress.md` 登记：任务 ID、改动文件、验证结果、未验证范围、下一项。

## 工程纪律

- 依赖方向：Domain ← Application ← Infrastructure；Contracts 独立；HostClient 被 Desktop/Cli/Mcp 引用。架构测试强制执行。
- 三入口（Desktop/CLI/MCP）只经 HostClient 操作业务，不直接开数据库、不各自启动扫描/游戏。
- 每个业务用例同步实现 CLI/MCP 映射（operation catalog：`contracts/operations.v1.json`）。
- 一个提交一个可验证行为增量；共享契约变更先登记版本/ADR。
- 测试不得遍历真实 F 盘；进程启动测试只用 `tests/GameLibrary.TestProcessStub`。
