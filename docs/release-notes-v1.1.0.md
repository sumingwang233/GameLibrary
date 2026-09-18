# GameLibrary v1.1.0

## 主要变化

- 普通游戏列表支持多选，并可批量收藏、取消收藏、设置标签或从库中移除。
- 自定义标签支持新建、改名、删除和筛选；列表可按名称、最近修改或最近入库排序。
- 启动方式可选择 EXE 或 SWF；SWF 由 Windows 当前关联的播放器打开。
- 导入封面按图片原始比例完整显示，不再裁掉边缘。
- 搜索刷新时保留当前详情，减少右侧详情区闪烁。
- 入库和修改时间统一显示为 `年-月-日 时:分:秒`。
- 使用指南移入设置，并补充三种翻译策略的简短说明。
- 启动时读取 GitHub 最新稳定 Release；发现新版本时提示用户打开下载页。
- 统一使用“游戏库”和“标签”称呼，并调整待确认游戏批量栏间距。

## 下载说明

- `GameLibrary-Setup-v1.1.0.exe`：推荐给普通 Windows 10/11 x64 用户，可选择安装位置，并在 Windows 已安装应用中提供卸载入口。
- `GameLibrary-Portable-win-x64-v1.1.0.zip`：免安装桌面版与后台 Host。
- `GameLibrary-Tools-win-x64-v1.1.0.zip`：CLI 与 MCP 工具。
- `GameLibrary-v1.1.0-SHA256SUMS.txt`：发布资产的 SHA-256 校验值。

## 签名状态

SignPath Foundation 申请仍在审核，本版本暂未进行 Authenticode 签名。Windows SmartScreen 可能提示未知发布者；请从本仓库 Release 下载并用 SHA-256 清单核对文件。
