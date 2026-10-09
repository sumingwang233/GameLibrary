<p align="center">
  <img src="assets/readme/hero.svg" width="960" alt="GameLibrary — Windows game library">
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a> · <a href="README.en.md">English</a> · <a href="README.ja.md">日本語</a>
</p>

# GameLibrary

把散落在硬盘里的游戏放到一处。选好文件夹，确认启动项，就能从游戏库打开游戏。

**[下载 Windows 安装版](https://github.com/sumingwang233/GameLibrary/releases/download/v1.7.5/GameLibrary-Setup-v1.7.5.exe)** · [所有版本](https://github.com/sumingwang233/GameLibrary/releases) · [反馈问题](https://github.com/sumingwang233/GameLibrary/issues)

[下载](#download) · [界面](#demo) · [开始使用](#start) · [常见问题](#faq)

<a id="download"></a>
## 下载

目前公开正式版为 **v1.7.5（未验收）**，面向 **Windows 10 / 11 x64**。维护者已明确豁免本次发布前验收；仅完成生产编译，测试、独立检查与人工验收均未执行。详见[本次记录](docs/releases/v1.7.5-validation.md)。

| 文件 | 适合谁 |
|---|---|
| [`GameLibrary-Setup-v1.7.5.exe`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.7.5/GameLibrary-Setup-v1.7.5.exe) | 推荐。当前用户安装器，可选择安装目录。 |
| [`GameLibrary-Portable-win-x64-v1.7.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.7.5/GameLibrary-Portable-win-x64-v1.7.5.zip) | 解压后运行 GameLibrary.Desktop.exe。 |
| [`GameLibrary-Tools-win-x64-v1.7.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.7.5/GameLibrary-Tools-win-x64-v1.7.5.zip) | Host、CLI 和 MCP，供命令行与自动化使用。 |
| [`GameLibrary-v1.7.5-SHA256SUMS.txt`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.7.5/GameLibrary-v1.7.5-SHA256SUMS.txt) | 上面三个下载包的 SHA-256 校验清单。 |

程序与安装器未进行 Authenticode 签名，Windows 可能提示未知发布者。请从本仓库下载，并用同版本校验清单核对文件：

```powershell
Get-FileHash .\GameLibrary-Setup-v1.7.5.exe -Algorithm SHA256
```

<a id="demo"></a>
## 看看界面

![游戏库：封面、搜索、收藏和侧边导航](website/public/assets/library.png)

![标签管理：整理游戏的分类与标签](website/public/assets/tags.png)

截图来自 v1.5.5 的隔离示例库，使用应用默认封面，不包含个人游戏目录。完整录屏尚未制作；[录屏脚本](docs/demo-recording.md)已准备好。

<a id="start"></a>
## 三步开始

1. 安装后打开 GameLibrary，添加存放游戏的文件夹。
2. 扫描结束后进入待审核列表，检查游戏名称、目录和启动文件，再确认入库。
3. 在游戏库中选择游戏并启动。需要时补充封面、收藏和标签。

扫描得到的是候选项，不会把找到的每个 EXE 都当成游戏。遇到未识别的游戏，也可以手动添加。

## 平时怎么用

| 事情 | 操作 |
|---|---|
| 找游戏 | 搜索名称，按标签筛选；常玩的游戏加收藏。 |
| 整理收藏 | 编辑封面与标签，在封面网格和列表之间切换。 |
| 配置启动 | 保留原始 EXE 入口，按需要设置启动参数、工具与翻译步骤。 |
| 使用脚本 | CLI 和 MCP 连接同一个本地 Host，操作同一份游戏库。 |

<a id="faq"></a>
## 数据与常见问题

**v1.7.6 待验收预发布**：修复模块化 Unity Mono 游戏离线补丁依赖解析，新增 IL2CPP x86/x64 翻译插件自动配置、恢复与启动确认。[下载与说明](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.6) · [验证记录](docs/releases/v1.7.6-validation.md)。

<details>
<summary><strong>扫描或移出游戏库会删除游戏文件吗？</strong></summary>

添加、扫描和移出记录保留原始文件。明确选择清理文件时会单独确认，并使用 Windows 回收站；请先核对目录。
</details>

<details>
<summary><strong>游戏库保存在哪里？卸载会清空吗？</strong></summary>

默认数据目录为 `%LOCALAPPDATA%\GameLibrary`，目录记录保存在本地 SQLite 中。卸载保留游戏库数据与原游戏文件。需要迁移时先备份，勿把程序目录当作游戏库备份。
</details>

<details>
<summary><strong>需要联网、账号或开发环境吗？</strong></summary>

整理本地游戏不需要账号，不上传游戏库遥测。检查更新时会访问 GitHub Release；启动的游戏和自行配置的工具可能联网。发布包自带 .NET 运行时，使用者无需安装 .NET SDK、Node.js 或 Rust。
</details>

<details>
<summary><strong>安装版、便携版和工具包有什么区别？如何更新？</strong></summary>

安装版有安装向导、开始菜单入口和卸载登记；便携版解压即可运行；工具包提供命令行与 MCP。更新前退出程序，安装新版或替换便携版的程序文件，保留数据目录。具体变化与限制以该版本 Release 说明为准。
</details>

<details>
<summary><strong>当前版本：v1.7.5，正式发布 / 未验收</strong></summary>

v1.7.5 根据架构审查收紧恢复后的旧写请求、启动幂等、备份封面一致性和文件回收恢复协议；统一 CLI/MCP 参数与 schema 发现，将慢注入等待移至后台作业。本次仅获授权进行打包编译，测试及人工验收未执行。参阅[架构实施](docs/development/v1.7.5-architecture-plan.md)、[版本说明](docs/releases/v1.7.5.md)和[验收记录](docs/releases/v1.7.5-validation.md)。

v1.7.4 修复 Unity 未翻译标签的自动配置触发，离线确认已有且启用的中文翻译插件后自动解除该标签；降低后台建议发现和候选核对的重复开销，退出桌面时优雅停止对应 Host，标签颜色仅用于文字和圆点。[版本说明](docs/releases/v1.7.4.md)记录变更，[验证记录](docs/releases/v1.7.4-validation.md)记录发布准备状态。

v1.7.3 修复旧资源 SWF 候选没有按目录归并、导致待确认数量过大的问题。首次启动立即核对受影响的扫描根，完整目录组代替旧逐文件候选；原游戏文件、ID 与元数据保留。继续使用 schema27，不重复重置忽略规则。保留 v1.7.2 的封面粘贴、Flash 审核、标签颜色和本机 Unity 翻译配置。见[验证记录](docs/releases/v1.7.3-validation.md)。

这些历史版本的功能已包含在上方 v1.7.5 正式包；旧说明和预发布链接保留供追溯。

v1.7.1 修复翻译后的副标题标点：原文含 `-` 时，将引擎返回的连续长破折号收为 `：` 并去掉首尾包围符。旧译名可在详情重新翻译；原文和手工译名保留。[v1.7.1 预发布包](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.1)及[验证记录](docs/releases/v1.7.1-validation.md)提供本次修复和验收状态。
</details>

<details>
<summary><strong>开发与项目结构</strong></summary>

开发需要 Windows、由 `global.json` 锁定的 .NET SDK 10.0.401、Node.js 22.12+ 和 Rust stable。安装 Rust 的 Windows MSVC 工具链及对应 C++ 构建工具。

```powershell
dotnet format --verify-no-changes
dotnet build GameLibrary.slnx -c Release
dotnet test GameLibrary.slnx -c Release --no-build
npm --prefix src/GameLibrary.Tauri ci
npm --prefix src/GameLibrary.Tauri test
npm --prefix src/GameLibrary.Tauri run typecheck
npm --prefix src/GameLibrary.Tauri run tauri dev
```

Tauri + React 是主桌面端，WPF 保留为迁移期后备界面。桌面端、CLI 和 MCP 经统一契约连接 .NET Host；Application 负责用例，Domain 负责业务规则，Infrastructure 负责 SQLite、文件和系统集成。[架构记录](docs/code-review-graph/architecture.md) · [操作目录](contracts/operations.v1.json) · [发布原则](docs/release-policy.md)

发布工具只在维护者明确要求打包时使用，不因一般代码修改自动发布。
</details>

## 参与和许可

欢迎在 [Issues](https://github.com/sumingwang233/GameLibrary/issues) 提交复现步骤、版本和错误信息；涉及个人路径的截图请先遮挡。提交代码前运行相关检查，共享操作改动需同步契约、客户端与测试。

[MIT License](LICENSE)
