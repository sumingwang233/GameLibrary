# contracts/ — 操作目录与契约治理

本目录是三入口（Desktop/CLI/MCP/IPC）唯一的操作契约来源。规格依据：《MCP与CLI原生接口契约.md》。

## 文件

- `operations.v1.json`：operation catalog（apiVersion=1）。每个操作登记 operationId、cli、mcpTool、handler、permission、requiresRevision、requiresIdempotencyKey、execution、status。
- `schemas/*.json`：随各任务落地的输入/输出 JSON Schema（当前尚未开始，由实现对应操作的任务补齐）。

## 命名规则（由 GameLibrary.ContractTests.OperationCatalogTests 机器校验）

- `operationId` = `{命名空间}.{动词}`，全部小写，多词动词用下划线（`import_plan`）。
- `mcpTool` = operationId 的 `.` 换 `_`（`backups.restore_plan` → `backups_restore_plan`）。
- `cli` = `[命名空间, 动词]`，动词下划线换连字符（→ `backups restore-plan`）。
- 输出字段 lowerCamelCase；ID 为不透明字符串；时间 UTC ISO 8601；枚举固定英文值。

## 状态语义

- `planned`：契约已登记、尚未实现。`tools/list`、CLI help 不得将其列为可用能力。
- `available`：已有 handler + CLI + MCP 映射 + 契约测试。发布门禁（AI-12/T28/T30）校验三入口一致。
- 翻转 `planned → available` 必须与实现同一提交。

## 治理规则

- 修改本目录 = 共享契约变更：先更新 schema/版本/兼容说明与消费方测试，再改生产者；已发布枚举/字段不得无版本静默重命名。
- 业务请求未知字段一律拒绝；响应新增可选字段由 Contracts 声明兼容策略。
- 接口负责人：当前单 agent 开发（负责人 sumingwang）；未来多 agent 协作时由负责人按模块分配文件所有权，共享契约变更须登记。
- 权限集合是应用权限（见 catalog `permissions`），不是 OS 权限；`none` 表示无需库授权（能力发现与宿主状态）。
- `desktop.*` 窗口操作为瞬时 UI 状态，无持久副作用，故无幂等收据；其余有副作用操作必须有持久幂等收据（ADR-0007）。

## 核心 ER 与状态机

实体关系摘要见《领域与可靠性补充规格》第 1.1 节；正交状态定义见 ADR-0005；实现落库时以 `GameLibrary.Domain` 的枚举为单一事实来源，其持久化值与本目录枚举拼写一致。
