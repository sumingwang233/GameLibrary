# GameLibrary 代码预览框架

v1.7.23 启动观察后续修复：沿唯一调用方确认主进程模块原先只读取一次；改为在既有轮询内、进程启动时间一致时重试。保留目录边界及 30 秒验证门槛；增量图谱错误 0、风险 low（0.33），本地 19 项生命周期测试和格式检查通过。Windows CI 结果见[验证记录](../releases/v1.7.23-validation.md)。

v1.7.23 全量检查后续修复：MCP 可空 JSON 参数通过请求上下文区分省略与显式 null；嵌套事务采用保存点；封面与扫描用例补齐必需参数；启动边界用例由验证状态控制退出。目录移至 D 盘后重建图谱：390 文件、3,947 节点、37,634 边，错误 0；本批变化风险 0.61。三项集成专项及真实 MCP stdio 的参数保留/清除与扫描用例通过；完整结果见[验证记录](../releases/v1.7.23-validation.md)。

2026-10-20 v1.7.23：修复 C# 格式与导入顺序，拆分 CI 格式 job，增加手动触发及 TRX 上传。修改前在独立 worktree 构建图谱（390 文件、3,946 节点、37,567 边）；修改后增量重解析 35 文件，错误 0。变更检测风险 0.75，包含格式变化引起的函数命中，不能据此认定业务逻辑改变。.NET Release 编译和 102 项前端测试已通过，完整 CI、打包与限制见[验证记录](../releases/v1.7.23-validation.md)。

2026-10-17 v1.7.22：Godot 内嵌 PCK 识别与 MTool GodotHook 翻译规则。修改前查询引擎检测、MTool 生成与启动路由影响上下文（候选 4 文件影响 45 文件、风险 high），修改后增量解析 15 文件，错误 0；图谱 387 文件、3,820 节点、36,695 边。真实样本只读验证 Godot/High/`godot.embedded-pack` 且手工 BAT 解析不 Broken；38 项 Unit、51 项 Integration、30 项契约、102 项前端与 Host Release 编译通过；真实游戏翻译效果、全量和人工验收未运行。见[实施与验证记录](../development/v1.7.22-godot-mtool.md)与[关键流程](flows.md)。未打包、推送或发布。

2026-10-17 v1.7.21：已游玩改为仅由用户在详情页手动标记。修改前查询 `MarkGamePlayed` 调用与 `launch.exited` 影响上下文，修改后增量解析 6 文件，错误 0；删除自动标记分支与失去调用方的 `MarkGamePlayed`（-2 节点、-11 边），图谱 387 文件、3,805 节点、36,500 边，执行流沿用历史统计。`detect_changes` 风险 0.64，其测试缺口为静态启发式——集成用例经 `WireAttemptPersistence` 夹具已覆盖，图谱未映射。11 项专项与 Host Release 编译通过；真实游戏、桌面和全量验收未运行。见[实施记录](../development/v1.7.21-manual-played.md)与[关键流程](flows.md)。未打包、推送或发布。

2026-10-17 v1.7.20：修复手动 EXE 回收范围和无入口资源目录误扫，补充 MTool 目录设置、BAT 启动及已评分游戏提示。修改前分别查询回收、扫描/审核和启动调用影响，修改后增量解析 37 文件，错误 0；图谱覆盖 387 文件、3,807 节点、36,511 边。170 个不同隔离专项用例、相关编译、生成与四语言检查通过，真实游戏/翻译 API、全量、桌面与安装器验收未运行。个人删除记录只读核查报告保留在本机。见[实施与验证记录](../development/v1.7.20-removal-scanning-and-launch.md)及[关键流程](flows.md)。未打包、推送或发布。

2026-10-17 v1.7.19：主库默认隐藏已游玩游戏，侧栏新增独立入口。沿 `Sidebar → useGamesQuery → GamesList → QueryGames` 查询影响并核对源码；修改后增量解析 23 文件，错误 0，图谱覆盖 386 文件、3,779 节点、35,961 边。筛选同时用于总数和分页，保留 CLI/MCP 缺省全库语义，无新 DDL、路由或依赖。66 项隔离专项检查、相关编译、生成一致性和四语言检查通过；真实桌面与全量验收未运行。见[实施记录](../development/v1.7.19-played-library.md)与[关键流程](flows.md)。本次未打包、推送或发布。

2026-10-17 v1.7.18 本地分发：维护者追加打包与源码提交授权。沿用二进制源码 `68edb90`，6 项生产编译通过、原始报告 11 项完整门禁 not-run；安装包未签名、未真机验收。四份资产已生成，包内和外部校验、ZIP 布局、四语言文档及根目录副本通过。仅同步发行文档与 README 源码说明，业务图谱保持 386 文件、3,773 节点、35,634 边。见[构建记录](../releases/v1.7.18-validation.md)及[版本说明](../releases/v1.7.18.md)；本次未创建 GitHub Release。

2026-10-17 v1.7.18：修改前分域查询翻译安装、游戏状态、手动建卡与共享检测影响；修改后增量重解析 71 文件，错误 0，图谱 386 文件、3,773 节点、35,634 边。修复 Mono 旧核心遮蔽端点；新增独立已游玩、五星评分与退出提示；删除侧栏收藏夹分组；手动建卡与未知游戏复用共享引擎证据，保持已知引擎和原收藏数据。213 个不同专项用例、Host Release 和前端生产编译、契约生成及四语言检查通过；真实游戏/API、全量和安装器验收未运行，本轮未打包、推送或发布。见[游戏体验与识别记录](../development/v1.7.18-game-experience-and-engines.md)、[Mono 根因](../development/v1.7.18-mono-endpoint-shadowing.md)和[关键流程](flows.md)。

2026-10-16 v1.7.17 MTool 配方生成：修改前查询启动处理器、配方发现和前端共享启动路径影响上下文；修改后分批增量解析，错误 0，图谱 379 文件、3,714 节点、34,685 边。新增标签触发、启动兜底、确定引擎的固定模板生成、旧路径备份修复，以及设置和详情状态；保持 plan 只读，先预检实际计划及其修订，缓存缺失不降级原文直启。112 项 .NET 专项和 33 项前端测试、编译/typecheck、操作生成与本地化检查通过。未执行真实游戏/API、全量或人工验收；未打包、推送或发布。见 [实施记录](../development/v1.7.17-mtool-preparation.md)及[关键流程](flows.md)。

2026-10-16 v1.7.16 分发交付：维护者追加授权打包、源码推送及 GitHub 发布。构建和 tag 沿用 `694583f`，包含 v1.7.15 封面筛选与 v1.7.16 MTool 修复；六项生产编译通过，十一项完整门禁明确 not-run，包未签名，未真机验收。四份资产已生成，包内与外部 SHA-256、ZIP 布局/CRC、根目录副本及远端摘要均通过，公开 HTTPS 校验清单下载一致。[GitHub 预发布](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.16)已公开，稳定最新版回读仍为 v1.7.13。源码未再修改，图谱保持 372 文件、3,651 节点、33,953 边；见[发行说明](../releases/v1.7.16.md)及[发行验证记录](../releases/v1.7.16-validation.md)。

2026-10-16 v1.7.16 MTool 启动：修改前查询配方解析、发现、路由及启动处理器影响上下文，修改后增量解析 10 文件、错误 0；372 文件、3,651 节点、33,953 边。修复真实生成脚本的静默前缀和缓存工作目录，保留生成入口并支持 Bakin 配方，预览与执行复用同一恢复建议。27 项解析、36 项管道/模拟启动/超时回归及编译、本地化通过；真实配方 15/15 可解析，其中 10 份仍有工具路径或缓存缺失。见 [实施与验证记录](../development/v1.7.16-mtool-launch.md)。未运行真实游戏/翻译工具，未打包、推送或发布。

2026-10-16 v1.7.15 封面筛选：修改前查询列表查询、收藏夹和 CLI/MCP 的影响上下文，修改后增量解析 31 文件，错误 0；372 文件、3,645 节点、33,868 边。沿 `LibraryToolbar → useGamesQuery → GamesHandler → QueryGames` 将当前封面条件同时应用于总数和分页；收藏夹复用 `filter_json`，无新数据库迁移或依赖。后端/契约 53 项、前端 16 项及相关编译、本地化、生成一致性检查通过。见 [实施与验证记录](../development/v1.7.15-cover-filter.md)。真实桌面验收及全量门禁未执行，本次未打包、推送或发布。

2026-10-14 v1.7.14 分发打包：维护者追加授权打包与 GitHub 上传，沿用源码和标签 `8ff1be3`。六项生产编译通过、十一项完整门禁明确 not-run，安装器与载荷未签名，尚未真机验收。三个分发包与校验清单已生成，草稿及公开状态下四个资产的名称、大小、SHA-256 均与本地一致；公开 HTTPS 清单下载核对通过，两个 ZIP 的六个载荷摘要及 CRC 通过。增量图谱无源码重解析、错误 0，保持 370 文件、3,629 节点、33,567 边。见 [发行说明](../releases/v1.7.14.md)与[发行验证记录](../releases/v1.7.14-validation.md)；[GitHub 预发布](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.14)已公开，正式最新版回读保持 v1.7.13。

2026-10-14 v1.7.14 开发交付：依据下述审查修复 R1–R9，完成详情重复 profiles 初载、选择回调和最近审计读取的小改动。修改前查询最小影响上下文，修改后增量同步新增回归与安装进程判定脚本；解析错误 0，370 文件、3,629 节点、33,567 边。后端专项 36/36、前端专项 56/56、相关编译/本地化/安装进程判定通过，完整发布门禁与真机验收未运行。见 [实施记录](../development/v1.7.14-safety-optimization.md)及[验证记录](../development/v1.7.14-validation.md)。本轮未打包、推送或发布，公开稳定版文案继续对应 v1.7.13。

2026-10-14 全仓静态审查：以 `a3fe0d133fb97367abefda751dc01a2f4b791a27` 为源码基线，主程序优先、正确性与可靠性优先、兼容小步优化。查询架构与调用图并回到源码核对，形成 9 项问题（3 项 P1、6 项 P2）及 7 组待测量的性能候选；包含架构图、文件级方案、迁移、回滚和验收标准。见 [深度审查与分阶段优化方案](deep-review-2026-10-14.md)。本轮仅新增报告及更新本索引，没有业务源码或产品版本变化，沿用原源码图谱；未运行测试、构建、lint、typecheck、性能或安装验证，未打包、推送或发布。

2026-10-13 v1.7.13 正式发布：维护者确认真机验收，沿用 `de4723e` 标签与原安装包。网站修改前查询影响上下文，修改后增量重解析 3 个源码文件、13 节点、219 边，解析错误 0；图谱覆盖保持 366 文件、3,590 节点、32,757 边。四语言 README 与网站下载源同步 v1.7.13；本次未补跑自动检查、重新打包或部署网站。见 [网站影响记录](website.md)和[发行记录](../releases/v1.7.13-validation.md)。

2026-10-12 v1.7.13：修改前查询最小上下文，修改后增量重解析 2 个源码文件，解析错误 0；图谱覆盖 366 文件、3,590 节点、32,757 边。沿 `FlashCandidateReviewService → fileGame → LaunchSuggestionService → AddSuggestions` 核对真实入库与公共序列化契约。独立文件入口直接建议，目录检测保留原预算，复用 Profile 持久化和删除抑制，无新 operation/DDL/依赖。见 [实施记录](../development/v1.7.13-flash-launch-suggestions.md)与[验证记录](../releases/v1.7.13-validation.md)。静态图谱不代表真实播放验收通过。

2026-10-11 v1.7.12：修改前查询最小上下文，修改后增量重解析 23 文件，边界收尾再解析 8 文件，解析错误 0；图谱覆盖 366 文件、3,589 节点、32,644 边。沿既有安装事务补 Mono BepInEx 6 入口契约、Unity 6 字体数组接口和原生 TMP 字体；后台修复保留 confirmed，左侧绑定标签仅按需选择单款验证并返回详情。新增兼容模块复用固定 Cecil，无新依赖/operation/DDL。见 [实施记录](../development/v1.7.12-unity-compatibility.md)及[验证记录](../development/v1.7.12-validation.md)。静态图谱不代表真实游戏验收通过。

2026-10-11 v1.7.11：修改前查询最小上下文，修改后增量重解析 16 个变更文件，收尾再同步 6 个文件，图谱覆盖 365 文件、3,570 节点、32,153 边。重点复核 `Inspect → ConfigureOne → Transaction` 和启动时修复的调用边；静态影响提示 6 个核心文件关联 22 个文件，不能代替运行验证。沿原队列修复 Unity 6 Interop、可验证双加载器协调、恢复重试和现代 Mono TMP 字体，不新增 operation/DDL/依赖。见 [实施记录](../development/v1.7.11-loader-compatibility-plan.md)及[真实验证记录](../development/v1.7.11-validation.md)。本次尚未打包或发布。

2026-10-11 v1.7.10：修改前查询最小上下文，追溯 Mono 字体消费者与详情标签更新。Mono 沿原字体事务配置 TMP 主字体，已有中文插件仍检查修复；详情拖放与下拉复用 updateTag 分类接口，无新依赖/DDL/operation。见 [实施记录](../development/v1.7.10-mono-fonts-and-tags.md)及[验证记录](../releases/v1.7.10-validation.md)。

2026-10-11 v1.7.9：修改前调用最小上下文，追踪 accepted-desc → AcceptCandidate 及按钮 → 配置队列 → Profile 推荐。复用可信建议入口与 Required 策略，接受后触发自动配置，详情复用共享状态。schema 28 仅按确认事件修复历史入库时间。见 [方案](../development/v1.7.9-acceptance-and-unity.md)及[验证记录](../releases/v1.7.9-validation.md)。

2026-10-09 v1.7.8：修改前查询图谱最小上下文，修改后增量解析共 7 个文件（含暂存新增字体模块），解析错误 0。沿既有安装流程补中文字体、旧中文配置迁移和本次失败回滚，无前端或数据库协议变化。56 项 Unity 专项通过。见 [方案](../development/v1.7.8-unity-fonts.md)与[验证记录](../releases/v1.7.8-validation.md)。

2026-10-09 v1.7.7：修改前调用最小上下文，追溯配置生产者与上游插件消费者；修复 BepInEx 实际 INI 路径，检查、写入、导入和启动路由复用同一布局。旧托管配置可无标签修复，拒绝/未托管状态不放宽。见 [实施记录](../development/v1.7.7-unity-config-path.md)与[验证记录](../releases/v1.7.7-validation.md)。

2026-10-09 v1.7.6：修改前调用最小上下文，围绕实际 Unity 默认入口追踪 tag → inspection → payload → transaction → launch route。新增跨模块枚举依赖的 Mono 补丁回归，以及固定包 IL2CPP x86/x64 配置、恢复和标签确认回归；复用已有队列、凭据和备份事务，无新 DDL。见[实施记录](../development/v1.7.6-unity-runtime-plan.md)与[验证记录](../releases/v1.7.6-validation.md)。

2026-10-06 v1.7.5：修改前最小上下文审计，修改后增量覆盖恢复、回执、备份、回收意图、共同参数和异步启动；新增 ArchitectureBoundariesTests 后再次索引。增量解析错误为空，静态图谱不代表测试通过。见[实施计划](../development/v1.7.5-architecture-plan.md)与[构建记录](../releases/v1.7.5-validation.md)。

2026-10-06 发布收尾：维护者明确豁免本次验收后正式发布 v1.7.5，四语言 README 与网站下载源同步版本及未验收说明；下载组件增量解析 3 个文件、错误 0。网站本次仅更新源码，未独立编译或部署。图谱计数保持下表，生产包与 tag 仍绑定 `c29a80f`。

2026-10-04 v1.7.4 收尾：已有中文 Unity 端点通过离线元数据检查后清理绑定标签；建议发现改为事件等待并预先过滤缓存命中，候选路径核对使用轻量投影；bridge 监视桌面父进程并请求对应 Host 停机，取消扫描且不等待占用的长轮询锁；标签仅保留文字/圆点颜色。修改前查询最小上下文，修改后25文件增量解析错误0，验证见 [v1.7.4](../releases/v1.7.4-validation.md)。

v1.7.4 修改前调用最小上下文，修改后增量刷新，解析错误0。UnityTranslationService 的六处引擎判断统一复用 IsActiveUnityGame，兼容扫描持久化的小写 unity；详情页恢复同样条件下的重试入口。测试默认使用真实引擎标识，新增启动/标签触发及详情按钮的大小写回归。接口、数据库与凭据存储未变；[验证记录](../unity-translation-backend.md)，流程见 [flows.md](flows.md)。

v1.7.3 修改前调用最小上下文，修改后增量刷新包含 FlashLegacyRegroupTests，解析错误0。新增旧逐文件 SWF 候选所属根查询、协调器即时核对及忙时重试、完整 inventory 内版本匹配归并、同组显式恢复和通知有效集合更新。无需新 schema 或 operation；流程见 [flows.md](flows.md)，真实数据库副本及发布验证见 [v1.7.3 验证记录](../releases/v1.7.3-validation.md)。

本文档是 `code-review-graph` 的可重复入口。它把源码结构、社区边界和执行流固定成一套低成本的预览流程，适合接手项目、代码评审和重构前审计。

## 当前快照

| 项目 | 结果 |
|---|---|
| 图谱基线 | v1.7.22 本地开发改动，2026-10-17；以 `2a70853` 为起点 |
| 源码文件 | 387 |
| 节点 / 边 | 3,820 / 36,695 |
| 执行流 | 175 |
| 解析错误 | 0 |
| 验证环境 | .NET SDK 10.0.401；global.json 未修改 |

快照在 2026-10-17 随 v1.7.20 增量更新，解析错误为空；直接 CALLS 8,270、未解析 CALLS 21,023。图谱中的委托、动态装配和裸名匹配仍需结合源码复核；`head_matches_build=true` 只说明 Git 提交基线一致，不证明工作区没有后续变化。175 个执行流是 2026-09-30 的历史统计，未在此次重新统计。网站的架构、素材与真实验证见 [网站实施记录](website.md)；此前记录见 [建议启动方式](../launch-suggestions.md)、[优化实施记录](optimization-implementation.md) 和 [v1.6.0 构建记录](../releases/v1.6.0-validation.md)。

## 入口文件

- [v1.7.14-safety-optimization.md](../development/v1.7.14-safety-optimization.md)：R1–R9 实施、兼容边界与性能暂缓项。
- [v1.7.14-validation.md](../development/v1.7.14-validation.md)：隔离专项测试、编译、安装进程判定与未运行范围。
- [deep-review-2026-10-14.md](deep-review-2026-10-14.md)：基于 v1.7.13 源码的全仓审查、可靠性问题、优化顺序和文件级迁移方案。

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
