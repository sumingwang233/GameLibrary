# GameLibrary v1.1.3

## 修复

- 修复 Desktop 左下角“待确认”数量把已接受、暂缓和忽略的历史候选一并统计的问题。
- `candidates.list` 支持按审核状态、扫描作业和分页参数筛选，`total` 返回过滤后的总数。
- CLI 与 MCP 同步暴露候选筛选/分页参数，以及游戏列表的搜索、收藏、视图、排序和分页参数。
- 核对 MCP stdio 首次调用链路；服务端协议和宿主启动正常，异常来自外部调用传入超限的会话读取参数。

## 下载

- `GameLibrary-Setup-v1.1.3.exe`：普通用户推荐的当前用户图形安装器。
- `GameLibrary-Portable-win-x64-v1.1.3.zip`：免安装 Desktop + Host 便携包。
- `GameLibrary-Tools-win-x64-v1.1.3.zip`：Host + CLI + MCP 工具包。
- `GameLibrary-v1.1.3-SHA256SUMS.txt`：发布资产 SHA-256 清单。

本版本 Windows 程序未进行 Authenticode 签名，SmartScreen 可能显示未知发布者。请从本仓库 Release 下载并核对 SHA-256。
