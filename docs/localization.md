# 界面语言

默认语言为简体中文；安装器、Tauri 主程序与保留的 WPF 客户端提供以下选项：

| 显示名称 | 主程序设置值 | NSIS 语言 ID |
| --- | --- | --- |
| 简体中文 | `zh-CN` | 2052 |
| 繁體中文 | `zh-TW` | 1028 |
| English | `en` | 1033 |
| 日本語 | `ja` | 1041 |

主程序在「设置 → 外观 → 语言」切换（WPF 在设置顶部选择后保存）。选择保存在当前游戏库的既有设置表中，重启后保留；重置设置、没有设置的旧库或损坏的语言值回落简体中文。Tauri 立即更新文字和托盘菜单；WPF 保存后更新主窗口、详情和托盘。设置变化事件也会刷新另一个客户端的语言。

安装器先显示语言选择窗口，首次默认简体中文。成功安装后记录选择，升级和卸载沿用该选择并允许重新选择。安装界面预览不会写入这项登记。安装语言和游戏库的界面语言分别保存，安装器不打开或改写用户的游戏库数据库。MIT 许可原文保留。

主程序共用 `src/GameLibrary.Contracts/Localization/ui.json`：键为简体中文原文，值依次为繁体中文、英文、日文。`{0}` 等占位符保留，WPF 的数字格式如 `{0:N0}` 也必须保留。原文空白归一化用于查表，玩家输入的名称、标签、路径和说明不经过翻译。服务错误中的原始诊断、日志、CLI/MCP 的机器数据和协议字段不随界面语言改写。

`settings.update` 新增可选 `uiLanguage`，复用 Revision、权限和幂等收据，未知语言值拒绝且不改设置。CLI 使用 `settings update --language ja --expected-revision <revision>`；MCP `settings_update` 使用 `uiLanguage`。catalog 与生成的输入 schema 已同步。

## 验证（2026-10-01）

- `python scripts/check_localization.py`：检查主程序词表、占位符、前端查表键、WPF 资源和 NSIS 四种语言覆盖；已接入 CI。
- Vitest 16 项通过：默认值、四种语言切换、HTML lang、设置控件及持久化、标签分组刷新、玩家名称保留、过期响应丢弃与卸载清理，以及既有前端检查。
- .NET：默认与重置、四种语言保存、无效输入拒绝、收据重放、CLI 参数、WPF 共享词表和带格式的插值；全量测试 565 项通过。
- NSIS：普通、覆盖安装及两种只读预览编译通过（警告视为错误）；四种语言分别通过隔离的安装、升级和卸载检查，验证语言登记及数据、未知文件保留。
- Tauri 前端 typecheck / 生产构建、Rust check / fmt、.NET Release 构建通过。

原生安装窗口的文字长度、日文字体回退及不同 DPI 下的视觉布局仍需实机查看。可使用 `artifacts/installer-ui/GameLibrary-Installer-Preview.exe` 和 `GameLibrary-Upgrade-Preview.exe` 检查，二者不实际安装程序。未推送或发布。
