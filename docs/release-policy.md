# GameLibrary 发布原则

确认日期：2026-10-01。适用于本仓库，后续发布以本文件为准。

## 版本与公开内容

- `Directory.Build.props` 的 Version 是产品版本真源。Tauri package.json、npm lock、Cargo.toml 和 Cargo.lock 的产品条目须一致；tag 使用 `vX.Y.Z`。
- 新功能使用 minor 版本，兼容修复使用 patch；破坏数据或契约兼容性时评估 major，并说明迁移方式。
- README 中文首页，维护繁體中文、English、日本語版本。下载和快速上手对应当前公开稳定版；源码新功能明确标为未发布。
- 截图和录屏使用隔离示例库、默认封面，标明实际软件版本和语言；不公开个人路径，不制作冒充真实操作的动画。
- 不覆盖稳定版 tag 或发布资产。发现问题发布新版本；预发布设置 prerelease=true、latest=false，完成验收后才设为稳定版。

## 发布包

| 名称 | 内容 |
|---|---|
| GameLibrary-Setup-vX.Y.Z.exe | 当前用户 NSIS 安装器，内含 Tauri Desktop、bridge、Host；不要求管理员权限。 |
| GameLibrary-Portable-win-x64-vX.Y.Z.zip | Desktop、bridge、Host 和包内 SHA256SUMS.txt，解压即可运行。 |
| GameLibrary-Tools-win-x64-vX.Y.Z.zip | Host、CLI、MCP 和包内 SHA256SUMS.txt。 |
| GameLibrary-vX.Y.Z-SHA256SUMS.txt | 上述三个最终分发包的 SHA-256。 |

- 当前只发布 Windows 10/11 x64。桌面运行时使用 WebView2，.NET 组件自包含。
- 两个 ZIP 根目录均为平面布局，不含重复条目、目录条目、PDB、辅助脚本、个人数据或开发缓存。
- 先签名程序（如配置了签名），再生成包内校验清单；安装器签名完成后，再生成外部清单。不把哈希当作发布者身份证明。
- 签名说明来自本次实际验证，不写固定申请结果或笼统的“已签名”。未签名时说明未知发布者提示和校验方法。

## 说明与证据

每个 Release 先简体中文、后 English，按 [模板](releases/TEMPLATE.md) 编写：摘要、主要变化、修复、升级与数据保留、下载、验证与限制。修复说明写触发条件和结果，不直接粘贴 commit 列表。

打包工具记录版本、源码提交、工作区是否有未提交修改、UTC 构建时间、每项命令的退出码和日志。状态分别为 passed、failed、not-run；禁止把未执行的检查写成通过。人工验收独立登记，不能从编译成功或测试数量推断。

稳定发布须完成：

1. operation 生成检查、四语言检查、README/Release 门禁、.NET format、Release build 与全量测试。
2. 前端测试、typecheck、生产构建、Rust fmt/check；最终 Tauri EXE 的 WebView 和批量操作验证。
3. 最终资产名称、ZIP 布局、包内及外部校验和、NSIS 编译与隔离安装／升级／卸载验证。
4. 干净 Windows 10/11 x64 环境（无另装 .NET）中的桌面启动；真实示例游戏扫描、审核与启动；四语言字体、100%/150%/200% DPI 和键盘操作。
5. 升级与卸载保留游戏库数据、原游戏和安装目录中的未知文件。

关键项失败或未执行时不得发布为稳定版。可以交付待验收构建；不得绕过检查再声称验证完成。

## 执行方式

```powershell
# 只检查源码与文档，不打包
python artifacts/tools/package_release.py --check
python scripts/check_release.py --self-test

# 仅在维护者明确要求打包时运行
python artifacts/tools/bootstrap_nsis.py
python artifacts/tools/package_release.py
python scripts/check_release.py --dist artifacts/dist

# 完成人工验收并如实登记后，稳定发布还需要此门禁
python scripts/check_release.py --dist artifacts/dist --stable
```

工具从自身位置确定当前仓库，不使用固定机器路径。输出位于本仓库 artifacts/dist，使用独立暂存目录；不能拿另一个检出的构建产物充数。可用 --makensis 指定已有编译器；不修改系统或用户环境变量。

本机已有应用运行、无法执行原生烟测时，可显式提供 --skip-desktop-smoke-reason 生成待验收包，记录 not-run 与原因。这不是稳定发布方式；--stable 必须拒绝。人工验收完成后，由维护者将报告 manualAcceptance 登记为 passed 并附验收证据；任何自动检查 failed/not-run 都仍阻止稳定发布。

完成检查后准备可审查差异、发布文案、包、清单和验证记录。推送、远程 Release、上传与正式部署需要维护者明确授权；已获得的本次授权无需重复询问。普通开发任务不会自动打包或发布。

发布后验证实际下载、HTTPS、校验和与更新入口，再将四语言 README 和网站的稳定版链接一起更新。旧包与旧说明保留，方便回退；回退程序前先备份数据，并确认数据库兼容性。
