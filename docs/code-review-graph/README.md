# GameLibrary 代码预览框架

2026-10-06 v1.7.5：修改前最小上下文审计，修改后增量覆盖恢复、回执、备份、回收意图、共同参数和异步启动；新增 ArchitectureBoundariesTests 后再次索引。增量解析错误为空，静态图谱不代表测试通过。见[实施计划](../development/v1.7.5-architecture-plan.md)与[构建记录](../releases/v1.7.5-validation.md)。

2026-10-06 发布收尾：维护者明确豁免本次验收后正式发布 v1.7.5，四语言 README 与网站下载源同步版本及未验收说明；下载组件增量解析 3 个文件、错误 0。网站本次仅更新源码，未独立编译或部署。图谱计数保持下表，生产包与 tag 仍绑定 `c29a80f`。

2026-10-04 v1.7.4 收尾：已有中文 Unity 端点通过离线元数据检查后清理绑定标签；建议发现改为事件等待并预先过滤缓存命中，候选路径核对使用轻量投影；bridge 监视桌面父进程并请求对应 Host 停机，取消扫描且不等待占用的长轮询锁；标签仅保留文字/圆点颜色。修改前查询最小上下文，修改后25文件增量解析错误0，验证见 [v1.7.4](../releases/v1.7.4-validation.md)。

v1.7.4 修改前调用最小上下文，修改后增量刷新，解析错误0。UnityTranslationService 的六处引擎判断统一复用 IsActiveUnityGame，兼容扫描持久化的小写 unity；详情页恢复同样条件下的重试入口。测试默认使用真实引擎标识，新增启动/标签触发及详情按钮的大小写回归。接口、数据库与凭据存储未变；[验证记录](../unity-translation-backend.md)，流程见 [flows.md](flows.md)。

v1.7.3 修改前调用最小上下文，修改后增量刷新包含 FlashLegacyRegroupTests，解析错误0。新增旧逐文件 SWF 候选所属根查询、协调器即时核对及忙时重试、完整 inventory 内版本匹配归并、同组显式恢复和通知有效集合更新。无需新 schema 或 operation；流程见 [flows.md](flows.md)，真实数据库副本及发布验证见 [v1.7.3 验证记录](../releases/v1.7.3-validation.md)。

本文档是 `code-review-graph` 的可重复入口。它把源码结构、社区边界和执行流固定成一套低成本的预览流程，适合接手项目、代码评审和重构前审计。

## 当前快照

| 项目 | 结果 |
|---|---|
| 图谱基线 | `9bc0082` + v1.7.5 工作区，2026-10-06 |
| 源码文件 | 362 |
| 节点 / 边 | 3,499 / 30,604 |
| 执行流 | 175 |
| 解析错误 | 0 |
| 验证环境 | .NET SDK 10.0.401；global.json 未修改 |

快照在 2026-10-06 增量更新，包含 v1.7.5 架构修复；解析错误为空。当前直接 CALLS 为 7,116，未解析 CALLS 为 17,552。图谱中的委托、动态装配和裸名匹配仍需结合源码复核；`head_matches_build=true` 只说明 Git 提交基线一致，不证明工作区没有后续变化。175 个执行流是 2026-09-30 的历史统计，未在此次重新统计。网站的架构、素材与真实验证见 [网站实施记录](website.md)；此前记录见 [建议启动方式](../launch-suggestions.md)、[优化实施记录](optimization-implementation.md) 和 [v1.6.0 构建记录](../releases/v1.6.0-validation.md)。

## 入口文件

v1.7.2 修改前查询最小上下文，修改后增量更新包括已暂存的新增文件和最后 MCP/像素收尾，解析错误0。新增 Flash inspector/review/store、Unity 配置服务与本机 vault/transaction、封面统一图片准备和前端两次确认流程。当前图谱有6,780条直接 CALLS、16,705条未解析 CALLS；仅把直接引用当作静态证据，不能从裸名边推断实际动态调度。更新时间2026-10-02T02:01:44，运行流见 [flows.md](flows.md)，真实测试与发布证据见 [v1.7.2 验证记录](../releases/v1.7.2-validation.md)。

v1.7.0 待验收预发布新增 [名称翻译](../title-translation.md)；本次调用插件的最小上下文、增量构建和影响评审，已索引新增存储、Host 服务和共享前端作业 Hook，解析错误 0。五个核心文件的图谱影响范围为 68 个文件 / 101 个节点；高风险评分来自共享 Host/存储入口，测试缺口是静态引用启发式，不能替代实际测试结果。打包前再次查询图谱确认源码基线 `51d5cd2` 一致，本次发布沿用既有打包流程，未修改业务代码或依赖。真实验证见 [v1.7.0 验证记录](../releases/v1.7.0-validation.md)。

- [architecture.md](architecture.md)：社区、依赖边界、架构图和高耦合告警。
- [website.md](website.md)：宣传站四语言、原型映射、真实素材、迁移/回滚与本地验收。
- [flows.md](flows.md)：Host 启动、桌面连接、扫描、事件轮询和启动游戏流程图。
- `.code-review-graph/wiki/index.md`：插件生成的社区 Wiki（本机生成目录，默认被 `.code-review-graph/.gitignore` 忽略）。

v1.7.1 修改前查询最小上下文，修改后增量重建及影响复核，解析错误 0。公共 `TitleTranslationClient` 校验 Google/Bing 结果后收紧由 ASCII 连字符生成的连续长破折号，再交给原有 CAS 保存；手工译名路径不变。翻译专项22项通过，正式打包检查与发布证据见 [v1.7.1 验证记录](../releases/v1.7.1-validation.md)。

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
