# ADR-0007 单宿主与三原生入口

- 状态：已接受
- 日期：2026-09-13（T20）

## 决策

1. 唯一 `GameLibrary.Host` 拥有数据库、扫描协调、启动执行、权限与作业；Desktop/CLI/MCP 是同级薄客户端，全部经 `HostClient`（Windows 命名管道，同用户）通信。MCP 不解析 CLI stdout，CLI 不调 MCP。
2. Contract-first：`contracts/operations.v1.json` 是唯一操作目录；每个操作有 operationId、CLI 名、MCP 工具名、输入/输出 schema、权限、Revision/幂等要求。新增产品能力先加 catalog 项与三入口映射，再实现 handler。
3. 统一结果信封（apiVersion/requestId/instance/epoch/ok/status/data/jobId/error/…）；status 固定六值；错误码是公开契约。
4. 每数据目录单宿主（用户 SID + 规范化数据目录摘要做互斥键 + 目录内锁文件）；客户端断开不终止 Host。
5. 数据目录由显式 `--data-dir`/部署配置提供，不推断 CWD；测试只连 `artifacts/test-runs/<guid>/data`。

## 理由

三入口若各自实现业务，规则/权限/事务会漂移；agent 无法在不打开 GUI 的机器上操作（AI-02）。契约目录让覆盖门禁（AI-12）可机器检查。

## 被排除方案

- CLI 直连数据库：绕过权限/事务/单写者。
- MCP 包装 CLI 文本输出：解析脆弱、退出码语义丢失。
- 各入口独立扫描/启动：重复 watcher、竞态写入。

## 不可变约束

- HostClient 不含业务规则；Desktop/Cli/Mcp 不引用 Infrastructure/SQLite。
- 任何入口不提供任意 Shell/SQL 后门；启动外部进程只能经 LaunchExecutor 的已验证计划。
- 有副作用操作必须有持久幂等收据；外部启动崩溃歧义返回 UnknownOutcome，禁止盲重试。

## 迁移影响

apiVersion=1；IPC 内部协议可升级但公开边界（CLI/MCP/schema）版本化，不兼容返回 HostVersionMismatch。

## 验证用例

S0 ping/tools/list 契约测试、AI-01…AI-12、T28 覆盖审计、T30 发布门禁。
