# GameLibrary 分阶段优化实施记录

日期：2026-09-30。基线：`fedc7ac103740482b4cf3a6b2567a3cc758ac709` 加本地工作区差异。交付源码、测试、生成器和审查记录；未提交或推送远程代码。SDK `10.0.401` 可用，`global.json` 未修改。

## 1. 请求并发与恢复安全

- `DispatchAsync` 保留单写准入、权限、Revision、epoch 和幂等收据；只读操作使用独立 SQLite 连接及读事务，计数、分页和附加字段共享快照。进入租约后再次校验 epoch，响应在租约释放前固定实例/epoch。
- 请求、普通作业、定时核对和进程退出回调持有库会话租约。恢复原子拒绝已有扫描、备份、核对和活动游戏；排空已经接收的请求后再次检查活动。忙碌错误可重试，不取消活动。停机也排空作业和租约后关闭连接。
- 候选列表只读。30 秒后台核对处理缺失候选、系统目录候选及通知确认，提交后发布变化事件；定时器异常进入宿主日志。
- 恢复先在目标目录的 `.restore-<id>` 内暂存数据库和资产，核验源/暂存清单并 flush；保留安全数据库备份和旧资产目录。数据库通过 `File.Replace` 切换，资产完整暂存后改名切换。
- `control/restore-in-progress.json` 原子记录 `prepared`、`database-swapped`、`assets-swapped` 和 `committed`。只有 `committed` 是提交点；提交前失败或重启回滚数据库和资产。回滚保留旧资产证据，可重复执行；安全备份或必要资产证据缺失进入 `RecoveryRequired`。
- 恢复后的 epoch 使用 FULL 同步、WAL checkpoint 和文件 flush 后才写提交记录。会话切换统一重绑事件、持久化回调、根目录、启动配置与视图状态；回调读取当前 Store。
- 控制区保存恢复请求摘要/结果和 `restore-jobs/<jobId>.json`。启动时补全提交后尚未保存的作业终态，后续恢复不会抹掉旧恢复结果。

恢复暂存目录、旧资产及安全备份保留供审查/人工回退，本轮没有自动删除策略。正常作业的取消契约保持现状，恢复作业不接受取消请求。

## 2. Application 与持久化分工

`GameCatalogService`、`CandidateReviewService`、`BackupCreateService` 和 `BackupRestoreService` 编排实际用例；Application 端口不依赖 IPC、SQLite 或 Infrastructure。公共编目/备份模型归入 Domain，Infrastructure 实现数据库和文件端口。Host 保留参数/权限、作业、事件和依赖装配。

`LibraryCatalogStore` 的候选、游戏、指纹、忽略规则 SQL 拆为四个 partial 文件；`SqliteLibraryStore` 保留连接、快照、单写事务及会话入口。沿用原有 SQL、迁移和业务修订语义。

## 3. 前端状态、事件与缓存

`LibraryProvider` 保持消费接口，内部拆为元数据、事件、扫描、标签、候选和分页游戏查询 hooks。Tauri 复用现有进程管理，普通请求和 `events.wait` 各用一个 Bridge/pipe 通道。

每次发布分配新事件序号；不覆盖已经发布的事件。前端按受影响域合并 250ms 内变化，保留分页、过期响应丢弃、卸载清理和退出尝试去重；旧 epoch/游标错误重建事件基线。取消待确认页的 15 秒全量刷新，扫描时列表仍可读取。

封面继续返回 data URL，以 key/value UTF-16 字符串估算占用，执行 64 MiB LRU 预算；超预算单项返回但不缓存，重复加载合并。资产变化和会话切换清缓存，失效前的加载结果不会污染新缓存。

发行脚本更新为当前标签浮层流程，处理前端与脚本初始化竞态，并校验两个 Bridge 子进程及长轮询期间的普通查询。详情页启动错误由详情页自身捕获并显示。

## 4. 批量与契约

| 操作 | 契约 |
|---|---|
| `backups.restore_start` | 现有恢复参数；立即返回 `accepted`/`jobId`，`jobs.get` 可查询最终 `result`，记录跨换库/重启保留 |
| `backups.restore` | 同一恢复实现，异步等待，保留原最终结果形状 |
| `events.wait` | 沿用 `events.read`，默认 25 秒，允许 0–30 秒；超时空批次，会话变化使等待游标失效 |
| `candidates.review_batch` | `action`、`items[{candidateId,expectedRevision}]`、幂等键；输入顺序逐项结果，业务失败回滚该项保存点，数据库异常回滚外层事务 |
| `tags.reorder` | `items[{tagId,expectedRevision,sortOrder}]`、幂等键；一项冲突则整批回滚 |

批量输入必须包含 1–1,000 项且 ID 唯一，Revision 为正整数，排序值非负。文件观察/指纹计算在事务外准备；事务内重新核对候选修订、状态、路径和负载，提交后发布事件。前端检查 capabilities，旧 Host 使用轮询和逐项操作。

`contracts/operations.v1.json` 同步新增操作、权限和紧凑参数元数据。Python 标准库生成操作常量/可用目录/简单输入描述、CLI 命令映射和简单 MCP 包装；特殊参数与结果加工保持手写。运行 `py -3 scripts/generate_operations.py --check` 校验生成文件、Host 路由和 CLI/MCP 映射，CI 同步执行。

## 验证结果

| 检查 | 结果 |
|---|---|
| `.NET format --verify-no-changes` | 通过 |
| `.NET Release build` | 通过，0 警告、0 错误 |
| `.NET 全量测试` | 563 通过：Contracts 49、Unit 152、Architecture 11、Headless 17、Integration 334 |
| 前端 Vitest / jsdom / Testing Library | 12 通过 |
| 前端 typecheck / production build | 通过 |
| 操作生成器 `--check` | 93 个可用操作、3 个生成文件一致 |
| Rust fmt / check / test --lib | 通过，现有原生测试 1 通过 |
| Host / Bridge self-contained 发布 | 通过 |
| Release WebView + batch smoke | 通过：窗口、详情标签、启动错误、批量收藏/取消收藏、标签、移除、根删除和原文件保留；两个 Bridge，无控制台子窗口 |

恢复测试在暂存、数据库切换、资产切换和提交检查点终止子进程，再启动并重复恢复，验证完整回滚或完整提交；另覆盖损坏备份、`RecoveryRequired`、控制结果跨重启/后续恢复、活动作业拒绝、同键重放。并发测试阻塞写连接时读取列表/事件/状态/诊断仍能完成，10 个同键批量请求只执行一次，换库唤醒并作废等待游标。批量测试覆盖成功项保留、冲突、致命数据库回滚、重复 ID、超限及收据重放。

前端测试覆盖游标、250ms 合并、旧 Host 回退、退出去重、分页保留、旧响应丢弃、卸载、批量失败和缓存预算/失效请求。

本机正在运行已安装的 GameLibrary；发行验证通过构建时子进程 `TAURI_CONFIG` 覆盖测试 identifier 为 `com.gamelibrary.optimization-smoke` 来隔离单实例互斥，未修改产品配置或系统/用户环境变量。验证数据与截图位于 `artifacts/optimization-smoke-final-v3/`；截图为 `release.png`。这次运行验证 Release 程序和 sidecars，没有重新生成/安装 MSI/NSIS 安装包。

## 图谱及审查

使用 code-review-graph 重建包括新文件的工作区图谱：300 个源码文件、2,818 个节点、21,526 条边、175 个执行流，无解析错误。新增文件使用 Git intent-to-add 以纳入本地差异/图谱，无已暂存内容。图谱审查上下文对本轮 91 个源码/配置/文档差异报告高影响范围：245 个节点、79 个文件；这是变更规模提示，不能代替行为验证。

`CandidateReviewService.ReviewBatch` 的直接调用边确认 `InTransaction → InReviewSavepoint → Apply`。Store 重绑经委托装配，图谱直接 callers 未解析到这一连接，已人工复核 `OperationDispatcher` 的 init 和 `BackupsHandler` 的恢复回调，不把空调用结果当作无影响。

Open Code Review 执行本地 `--preview --audience agent`，预览记录位于 `artifacts/optimization-smoke-final-v3/ocr-preview.md`；没有调用付费 Provider，没有新增模型审查结论。历史 OCR 结论保留在 [代码审查报告](code-review-report.md)。本轮人工复核及回归修正了 epoch 准入/响应竞态、退出回调租约、恢复终态写入窗口、通知清理顺序和详情启动错误传播。

## 权限及保留边界

维持 Windows 用户账户信任模型：每库 named pipe、库路径规范化、单宿主守卫、客户端声明权限、Revision/epoch/幂等共同约束误用；省略权限集仍兼容第一方客户端，`access.admin` 不作为自声明通配符。同一 Windows 账户下的进程并不因此相互隔离；能直接访问库/控制文件的进程也在账户信任边界内。

WPF 保留。退役条件：Tauri 功能对等，安装包和便携包验证通过，且连续两个稳定版本没有必须依赖 WPF 的功能缺口。完整 DTO schema、通用状态框架、本地资产协议均未纳入本轮。
