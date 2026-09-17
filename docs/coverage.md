# 三入口覆盖报告（T28 / AI-12）

数据来源：`contracts/operations.v1.json`（129 操作）× `GameLibrary.Contracts.OperationCatalog.ImplementedOperations`（代码声明）。
机器校验：`ThreeEntranceCoverageTests`（MCP 反射 / CLI 解析 / dispatcher 空参数探针四门禁）。

## 汇总

| 状态 | 数量 |
|---|---|
| 已实现（三入口齐备，门禁锁定） | 89 |
| 已登记未实现（planned，不出现在 tools/list） | 40 |

## 已实现（按命名空间）

- **capabilities/schema/host**：capabilities.get、schema.get、host.status、host.stop
- **library**：library.init（status/export/import_plan/import 未实现）
- **roots**：roots.add、roots.list、roots.remove（update/rebind 未实现）
- **scan**：scan.start/status/cancel/coverage/inspect（pause/resume 未实现）
- **candidates**：list/get/accept/defer/ignore（全）
- **games**：list/get/create/update/relink/remove（全；手动目录/EXE/SWF/LNK 入库与非删文件移除）
- **fields / assets / metadata**：set/clear/reset；import/list/get/choose/crop/reset/remove；preview/refresh（全）
- **ignores**：list/create/remove（update 未实现）
- **tags**：list/create/update/remove/assign/unassign/suppress/reset（全；T-collections 阶段三）
- **profiles**：list/get/create/update/set_default/remove/validate（全）
- **translation**：get/set（全）
- **tools**：discover（list/get/register/update/remove/capabilities 未实现）
- **verification**：start/get/list/report/invalidate（全）
- **launch**：plan/execute/status/history（全）
- **jobs**：get（list/wait/cancel 未实现）
- **events**：read（全）
- **notifications**：list/get/acknowledge/defer（全）
- **settings**：get/update/reset（全）
- **views**：list/get/create/update/remove/activate（全）
- **diagnostics**：status/logs/cache_rebuild（export 未实现）

> schema.get 返回程序化生成的真实 inputSchema/outputSchema（Contracts.OperationSchemas，draft 2020-12 子集）。
> games.list 的搜索/过滤/排序/分页已下沉数据库侧（QueryGames，阶段三），tagId 过滤与 tags.* 联动。

## 未实现（planned，契约已登记）

host.start*、library.status/export/import_plan/import、roots.update/rebind、scan.pause/resume、rules.×6、tools.list/get/register/update/remove/capabilities、jobs.list/wait/cancel、desktop.×5、backups.×5、access.×4、actions.×3、diagnostics.export

\* host.start 的语义（连接引导层按需拉起宿主）已由 HostProcessLauncher 在 CLI/MCP/Desktop 连接时实现，未注册为独立可调用操作。

## 与门禁的关系

- 新增操作必须先登记 catalog 再实现；翻转"实现"的唯一途径是向 ImplementedOperations 集合添加 ID（同一提交内补齐三入口）。
- `ThreeEntranceCoverageTests` 任何一项失败即发布门禁失败（AI-12）。
