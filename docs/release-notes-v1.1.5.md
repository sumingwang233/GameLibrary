# GameLibrary v1.1.5

## 全新桌面界面

- 主桌面端迁移到 Tauri v2、React 19、TypeScript、Tailwind CSS v4 与 shadcn/ui。
- 使用 Steam 风格深色游戏库：232px 左侧导航、自适应封面网格、右侧详情 Sheet、搜索、排序、收藏和标签筛选。
- 支持紧凑列表、加载骨架、空状态、封面预览和大量游戏的 `content-visibility` 优化。

## 功能与修复

- 保留现有 .NET Host、SQLite 数据与操作契约；React 通过轻量 .NET bridge 连接同一 Named Pipe Host。
- 新界面支持扫描/取消、待确认批量审核、手动添加、原始 EXE 启动配置、封面、标签、目录和设置。
- 修复扫描进度候选含义与大目录取消延迟。
- `[toolNeed]` / Auto 游戏可从受支持的 MTool 配方自动启动注入器和翻译工具，无需配置“已翻译游戏程序”。

## 下载

- `GameLibrary-Setup-v1.1.5.exe`：推荐普通用户使用的当前用户图形安装器。
- `GameLibrary-Portable-win-x64-v1.1.5.zip`：Tauri Desktop + .NET bridge + Host 免安装包。
- `GameLibrary-Tools-win-x64-v1.1.5.zip`：Host + CLI + MCP 工具包。
- `GameLibrary-v1.1.5-SHA256SUMS.txt`：发布资产 SHA-256 清单。

本版本 Windows 程序未进行 Authenticode 签名，SmartScreen 可能显示未知发布者。请从本仓库 Release 下载并核对 SHA-256。
