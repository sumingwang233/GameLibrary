# GameLibrary 代码预览框架

本文档是 `code-review-graph` 的可重复入口。它把源码结构、社区边界和执行流固定成一套低成本的预览流程，适合接手项目、代码评审和重构前审计。

## 当前快照

| 项目 | 结果 |
|---|---|
| 图谱基线 | `49fc57d` + v1.7.0 工作区，2026-10-01 |
| 源码文件 | 341 |
| 节点 / 边 | 3,118 / 25,471 |
| 执行流 | 175 |
| 解析错误 | 0 |
| 验证环境 | .NET SDK 10.0.401；global.json 未修改 |

快照在 2026-10-01 增量更新，包含 v1.6.1 建议启动方式、进程观察、持久化与前端修改，解析错误为空。图谱中的委托、动态装配和裸名匹配仍需结合源码复核；`head_matches_build=true` 只说明 Git 提交基线一致，不证明工作区没有后续变化。175 个执行流是 2026-09-30 的历史统计，未在此次重新统计。实施及验证见 [建议启动方式](../launch-suggestions.md)，此前记录见 [优化实施记录](optimization-implementation.md) 和 [v1.6.0 构建记录](../releases/v1.6.0-validation.md)。

## 入口文件

v1.7.0 待验收预发布新增 [名称翻译](../title-translation.md)；本次调用插件的最小上下文、增量构建和影响评审，已索引新增存储、Host 服务和共享前端作业 Hook，解析错误 0。五个核心文件的图谱影响范围为 68 个文件 / 101 个节点；高风险评分来自共享 Host/存储入口，测试缺口是静态引用启发式，不能替代实际测试结果。打包前再次查询图谱确认源码基线 `51d5cd2` 一致，本次发布沿用既有打包流程，未修改业务代码或依赖。真实验证见 [v1.7.0 验证记录](../releases/v1.7.0-validation.md)。

- [architecture.md](architecture.md)：社区、依赖边界、架构图和高耦合告警。
- [flows.md](flows.md)：Host 启动、桌面连接、扫描、事件轮询和启动游戏流程图。
- `.code-review-graph/wiki/index.md`：插件生成的社区 Wiki（本机生成目录，默认被 `.code-review-graph/.gitignore` 忽略）。

## 刷新流程

在仓库根目录调用 `code-review-graph` MCP：

1. `build_or_update_graph_tool(repo_root="D:\\Official\\GameLibrary")`
2. `get_minimal_context_tool(task="<本次审计问题>")`
3. `get_architecture_overview_tool(detail_level="minimal")`
4. `list_flows_tool(detail_level="minimal")`
5. 对需要展开的入口调用 `get_flow_tool(flow_name="<入口名>")`
6. `generate_wiki_tool(force=true)` 更新本地 Wiki。

增量更新默认只处理自上次图谱提交以来变化的文件。发生大规模目录或解析器变更时再使用 `full_rebuild=true`。不要把 `.code-review-graph` 下的 SQLite、Wiki 或运行缓存当作业务源码提交。

## 预览约定

- 架构图使用社区级边，边数来自图谱的 `cross_community_edges`。
- 流程图只保留入口、边界和持久化/事件关键节点，完整调用链通过 `get_flow_tool` 追溯。
- 节点名后面的文件与行号是图谱快照证据；源码变更后必须重新构建图谱再引用。
- `CALLS` 解析为 `unresolved` 的节点只作为待确认线索，不直接作为重构依据。

## 建议的评审顺序

1. 从 `get_minimal_context_tool` 确认风险、关键实体和受影响流程。
2. 查看 [architecture.md](architecture.md) 的高耦合社区和桥接节点。
3. 对目标入口展开 `get_flow_tool`，再用 `query_graph_tool` 查询 callers/callees。
4. 修改后重新执行增量图谱、`detect_changes_tool` 和受影响流程查询。
5. 最后运行项目既有的 .NET、前端和 Rust 验证门禁。
