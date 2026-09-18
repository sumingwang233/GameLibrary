# GameLibrary

面向用户与 AI agent 的 Windows 本地游戏库。当前状态：程序已实现并按《v1 审查意见稿》完成第一/二阶段修复与第三阶段主体——Host 单写队列、库会话切换、库根/Profile/启动历史持久化、标签（tags.×8，含自动引擎标签与 Suppress 语义）、数据库侧检索与分页（5000 游戏性能基线达标）；Desktop 支持首次双击即用、添加游戏文件夹、扫描、审核、手动添加与启动游戏、首用指南、字体/主题及安全的封面缓存位置设置。三入口（Desktop/CLI/MCP）同库同契约，89/129 操作已实现并经覆盖门禁锁定；发布包与校验清单见 `artifacts/dist/`。

工程目录：`D:\Official\GameLibrary`。本机日常数据规划为 `D:\Official\GameLibrary\LocalData`，开发测试使用独立目录。项目、命名空间与发布组件统一使用 GameLibrary，不使用 Local 前缀。

## 下载与安装

正式版本发布在 [GitHub Releases](https://github.com/sumingwang233/GameLibrary/releases)。普通 Windows 10/11 x64 用户只需下载并运行 `GameLibrary-Setup-v*.exe`；NSIS 图形化安装向导内含桌面程序和后台 Host、自包含 .NET 运行时，可选择程序安装位置，不要求管理员权限或打开终端窗口。安装完成后，安装目录包含 `Uninstall.exe`，Windows“设置 → 应用 → 已安装的应用”和开始菜单也提供卸载入口；安装器只写当前用户的标准卸载登记，不修改系统环境变量或安装服务，卸载时保留默认游戏库数据。免安装用户可下载 `GameLibrary-Portable-win-x64-v*.zip`，其中只有两个单文件 EXE 与校验文件。CLI/MCP 放在独立的 `GameLibrary-Tools-win-x64-v*.zip`，不混入普通用户包。两个 ZIP 都不附带 PowerShell/CMD 脚本。发布资产的 SHA-256 见同版本 `GameLibrary-v*-SHA256SUMS.txt`。

## 代码签名与隐私

正式 Windows 发布产物通过 SignPath.io 签名，证书由 SignPath Foundation 提供。签名流程、负责人及验证方式见[代码签名政策](docs/code-signing-policy.md)。GameLibrary 不包含遥测，不会自动向网络服务上传游戏库、扫描结果或使用数据；只有用户明确配置和启动的外部程序可以执行其自身的网络行为。

本项目采用 [MIT License](LICENSE)。

## 开发阅读顺序

1. [主策划案 v1.1](<D:/Official/GameLibrary/开发策划案 v1.md>)：产品范围、F 盘调研、识别/启动逻辑、工程架构。文件名保留 v1，以兼容现有笔记。
2. [领域与可靠性补充规格](<D:/Official/GameLibrary/领域与可靠性补充规格.md>)：实体与身份、正交状态、扫描证据、适配器副作用、性能/恢复验收。
3. [MCP 与 CLI 原生接口契约](<D:/Official/GameLibrary/MCP与CLI原生接口契约.md>)：三入口全能力矩阵、单宿主、schema、权限、幂等、Job/事件、恢复语义。
4. [开发 Agent 任务书](<D:/Official/GameLibrary/开发Agent任务书.md>)：31 项任务及依赖、开工提示词、交付与验收。

[策划案修正建议](<D:/Official/GameLibrary/策划案修正建议.md>) 是用户提供的评审输入，原文件保留。采纳与调整的理由在“领域与可靠性补充规格”第 7 节，不把未经调整的建议示例作为另一套实现标准。

## 约束优先级

用户明确的新要求优先；原生接口细节按接口契约，领域/状态/可靠性细节按补充规格，其他产品行为按主策划案；任务书只是执行拆解，不另定义冲突状态或路径。

GUI、CLI、MCP 必须经同一 Host 操作。每实现一个业务用例同步交付原生接口，不能在发布后补 MCP。F 盘是外部游戏数据，扫描不写游戏文件。游戏指纹不作永久 ID；翻译工具窗口出现不代表翻译成功；外部工具写入声明不冒充 OS 沙箱。
