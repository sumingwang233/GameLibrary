本文件是 [开发策划案 v1.1](<D:/Official/GameLibrary/开发策划案 v1.md>) 的强制实施规格。项目根目录：`D:\Official\GameLibrary`。这里的命令、工具和 schema 是待实现的公开契约，不代表当前已存在可运行程序。

## 1. 产品原则：程序能力对人和 agent 等价

GUI、CLI、MCP 是三个同级入口。全部 GameLibrary 产品操作必须拥有原生应用用例、CLI 命令、MCP Tool 以及结构化查询结果。agent 不需要定位窗口、识别截图、点击菜单、编辑 SQLite 或生成脚本来代替正常操作。

“操作一切”包括库、扫描、资料、封面、标签、分类规则、候选审核、忽略、根目录、启动配置、翻译工具、验证记录、通知、作业、设置、备份恢复、导出诊断以及界面视图状态。平台窗口的原始鼠标事件不属于业务接口；选中游戏、打开详情、修改筛选等有明确语义接口。

外部游戏玩法、第三方翻译软件的登录/付费/拖入流程不属于 GameLibrary 自有能力。当前缺乏可靠自动化协议时，返回结构化 NeedsUserAction，允许查询与继续配置，不伪造成功，也不为此暴露任意系统命令执行。

## 2. 一宿主、多客户端

```text
GameLibrary.Desktop.exe ─┐
gamelibrary.exe         ─┼→ HostClient → Windows 命名管道 → GameLibrary.Host.exe
GameLibrary.Mcp.exe    ─┘                                   ├ Application handlers
  ↑ 标准 MCP stdio                                         ├ SQLite / Jobs / Events
任意兼容 MCP 客户端                                        └ Scanner / LaunchExecutor
```

Host 保持业务模块化单体。Desktop、CLI、MCP 不能打开数据库连接、创建 watcher、直接执行游戏。HostClient 不含业务规则。MCP 直接映射 HostClient，不经 CLI 的 stdout 反解析；CLI 也不调用 MCP 当内部 RPC。

### 2.1 启动与数据目录

- 本机日常数据目录为 `D:\Official\GameLibrary\LocalData`。启动参数 `--data-dir` 必须是绝对路径，或由三入口共用的部署配置提供；不从 CWD 推断。不允许在配置缺失时各自创建数据库。
- 有明确数据目录时，正常 CLI/MCP/GUI 可按需拉起已部署 Host，再连接；首次建库需执行明确的 `library.init` 操作。已有库按需启动不视为扫描/启动游戏授权。
- `host status --no-start` 只检查状态，不启动宿主、不创建目录或库。`capabilities`/schema 离线可返回静态编译契约，标注 Host 未连接；运行能力必须由 Host 返回。
- Host 进程由客户端安装目录中的固定二进制启动，不搜索 PATH、不执行配置中的任意命令；启动时使用隐藏窗口并设置准确工作目录。超时最多 10 秒返回 HostUnavailable，不无限等待。
- 启动互斥键使用当前用户 SID 和规范化数据目录摘要。拿锁后再尝试连接已有 Host，处理多个客户端同时冷启动。宿主持有命名 Mutex，并对真实数据目录持有独占锁文件句柄；锁属于目录中的固定文件，避免路径别名造成两个写入者。
- 数据目录比较须处理 `..`、大小写、尾分隔符及最终目录别名；在目录不存在时先做规范化，library.init 创建后重新解析。不能只 hash 原始参数文本。
- 单数据目录一个 Host；多个数据目录可分别运行，所有响应包含 libraryInstanceId/dataEpoch，客户端不能将结果混用。
- 客户端断开不终止 Host；桌面关闭不使 API 下线；Host stop 先拒绝新写任务、持久化作业与收据，再排空队列、关闭连接。正常 stop 不杀游戏/翻译器。

### 2.2 IPC 边界

使用 Windows 命名管道，仅同用户连接，使用 CurrentUserOnly 与相应 ACL；验证远端连接和进程身份，禁止网络远程管道访问。CurrentUserOnly 也涉及 Windows 提权等级，故所有组件默认非管理员、相同完整性等级；不自动提权。[Microsoft PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)。

管道协议采用长度前缀 UTF-8 JSON 消息，单消息上限 4 MiB、明确的 requestId、apiVersion、operationId、参数和请求上下文。多请求可并发读，变更仍由宿主协调。大图/全库导出走有界资源或作业产物，不能把数百 MiB 塞进 IPC。

每连接进行版本和实例握手；不要公开 IPC 字节流给外部 agent 当稳定接口。公开稳定边界是 CLI、MCP 与 Contracts 的 operation schema。内部 IPC 可升级，但不兼容时明确返回 HostVersionMismatch；不同版本宿主存在时不自动终止或替换。

同一 Windows 用户下的恶意程序本来即可读用户文件，命名管道不是隔离同用户恶意代码的安全沙箱。连接器权限主要防止误操作和模型越权，不能把客户端自报 actor/clientName 当身份认证。

## 3. 契约优先与能力目录

实现时在 `contracts/operations.v1.json` 维护 operation catalog，每项至少包含：

```json
{
  "operationId": "games.update",
  "apiVersion": "1",
  "cli": ["games", "update"],
  "mcpTool": "games_update",
  "handler": "UpdateGameHandler",
  "inputSchema": "schemas/games.update.input.v1.json",
  "outputSchema": "schemas/operation-result.v1.json",
  "permission": "library.write",
  "requiresRevision": true,
  "requiresIdempotencyKey": true,
  "execution": "sync",
  "uiCommand": "EditGame.Save"
}
```

该片段是示例条目。Catalog 中须列出实际操作，而不是只有上面一个条目。请求类型、JSON Schema、CLI 参数绑定与 MCP schema 应从同一 C# Contracts 类型生成/核对；不要手抄三套验证逻辑。

工具名由 `operationId` 的点改为下划线；CLI 为名词加动词，动词中的下划线改连字符，例如 `backups.restore_plan` → `backups_restore_plan` → `backups restore-plan`。输出字段 lowerCamelCase，ID 为不透明字符串，时间 UTC ISO 8601，枚举使用固定英文值。

`capabilities.get` 返回产品/协议/schema 版本、可用操作与权限、宿主状态、已验证适配器、路径限制、分页上限、是否支持取消/订阅等。`schema.get(operationId)` 返回完整请求与结果 schema。未实施能力不出现在“可用”集合里，也不能用空实现充数。

### 3.1 必须覆盖的操作矩阵

表中每个动词均为独立 catalog 项与 MCP Tool，不能仅实现表中的第一项。UI 不需要为诊断 ping 等设置按钮，但所有产品交互必须映射到同一能力；系统诊断标记为 InterfaceOnly，并给出原因。

| 命名空间 | 操作动词 | 语义与约束 |
|---|---|---|
| `capabilities` / `schema` | get / get | 发现可用能力、请求/响应类型、错误和权限 |
| `host` | status, start, stop | status 支持不拉起；stop 返回已接收收据后关闭；本地启动可由连接引导层处理 |
| `library` | init, status, export, import_plan, import | 建库、状态、版本化便携库导出/导入；导入有冲突预览和路径重新绑定，不运行其中程序 |
| `roots` | list, get, add, update, remove, rebind | 根路径、启用/禁用、排除；remove 仅解除监控，现有游戏保留并提示 RootUnbound |
| `rules` | list, get, create, update, remove, preview | 分类/标签/策略规则；预览影响后更新，不移动游戏 |
| `scan` | start, status, pause, resume, cancel, coverage, inspect | start 返回 Job；inspect 是只读单路径识别；覆盖结果可分页 |
| `candidates` | list, get, accept, defer, ignore | 初次和新增候选审核，支持明确 ID 批次；扫描本身不直接批准入库 |
| `ignores` | list, get, create, update, remove | 忽略记录可撤销；忽略子树和忽略单项区分 |
| `games` | list, get, create, update, remove, relink | 手动添加与路径重新关联；remove 仅删库记录并写忽略；默认不删除游戏文件 |
| `fields` | set, clear, reset | 用户设值、主动清空、恢复自动值，不能都变成 null |
| `tags` | list, create, update, remove, assign, unassign, suppress, reset | 全局标签、逐游戏多来源与覆盖；删除全局标签必须返回受影响项 |
| `metadata` | preview, refresh | 提取规则资料、来源证据、选择接受；不覆盖用户层、不调用付费模型 |
| `assets` | list, get, import, choose, crop, reset, remove | 本地图片、候选封面、裁切参数；读图片返回受限预览；删除仅限应用自有未引用资源 |
| `profiles` | list, get, create, update, remove, set_default, validate | 入口、argv 数组、cwd、工具绑定；默认配置移除需明确替代项 |
| `translation` | get, set | Auto/Required/NotRequired 与来源；不直接修改显示标签替代策略 |
| `tools` | list, get, discover, register, update, remove, capabilities | 工具路径发现/绑定、可用能力与指纹；discover 仅读允许路径，不自启动工具 |
| `verification` | start, get, list, report, invalidate | 已授权样本验证、查询、记录用户观察、验证失效；报告不等于自动证实翻译生效 |
| `launch` | plan, execute, status, history | 纯计划、执行、查询结果；不能以任意 EXE 路径跳过 Profile 校验 |
| `jobs` | list, get, wait, cancel | 统一长任务状态；wait 有最大时限与游标，不要求 MCP 客户端支持扩展任务协议 |
| `events` | read | 按持久游标增量读取实体/扫描/启动/通知变化 |
| `notifications` | list, get, acknowledge, defer | agent 原生处理通知；ack 不等于接受关联游戏候选 |
| `settings` | get, update, reset | 扫描频率、通知、主题、托盘、开机启动等；系统副作用按相同授权执行 |
| `views` | list, get, create, update, remove, activate | 搜索、筛选、排序、收藏视图、当前选中游戏；存为语义状态 |
| `desktop` | state, show, navigate, hide, close | 仅控制 GameLibrary 的窗口与详情页；无窗口时返回 NotRunning 或显式启动 Desktop |
| `backups` | list, create, inspect, restore_plan, restore | 一致快照、完整性核查、影响预览、维护模式恢复 |
| `diagnostics` | status, logs, export, cache_rebuild | 脱敏日志、诊断包、重建可再生缓存；不能清理用户原图/游戏 |
| `access` | status, list, configure, revoke | 查看有效授权；configure/revoke 需要宿主已核实的 owner/admin 上下文，普通 agent 无自升权 |
| `actions` | list, get, complete | 查询待处理动作与提交结果；不能把 complete 当授权或翻译成功证明 |

常用“收藏、取消收藏”是 `games.update` 中明确的 `favorite` 字段；排序、布局和主题归 views/settings。重复功能不必额外造同义命令。若开发新菜单涉及本表外能力，必须先增加 catalog 项和三入口映射。

## 4. 共用请求、结果与错误

请求上下文包含 requestId、可选 idempotencyKey、expectedRevision、operationId，以及由宿主识别的 actor。对现有库的变更必须携带握手取得的 libraryInstanceId 与 expectedDataEpoch；宿主先校验实例/epoch，再处理版本与收据。初次 library.init 和不依赖库的查询使用单独引导契约。调用者声明的 clientName 仅作审计标签。用户资料文本始终是数据，不可成为系统指令或授权证据。

```json
{
  "apiVersion": "1",
  "requestId": "req-demo-01",
  "libraryInstanceId": "library-demo",
  "dataEpoch": "epoch-demo",
  "ok": true,
  "status": "completed",
  "data": {"gameId": "game-demo", "revision": 8},
  "jobId": null,
  "error": null,
  "warnings": [],
  "nextActions": []
}
```

`status`：completed、accepted、partial、needsUserAction、failed、unknownOutcome。`accepted` 只代表作业已入队，不等于目标业务完成；partial 表示批次/覆盖存在缺口，ok=false，逐项结果和覆盖必须提供。查询一个未完成 Job 本身可以是 completed，执行状态在 data.job.state 中，不混淆查询成功与任务成功。错误对象含 code、message、retryable、fieldErrors、currentRevision、recoveryOperation；可选字段缺省为 null，不在 message 中塞入唯一可用修复信息。

错误至少覆盖原策划错误码，新增：InvalidArgument、UnsupportedOperation、RevisionConflict、IdempotencyConflict、PermissionDenied、NeedsAuthorization、HostUnavailable、HostVersionMismatch、DataDirectoryMismatch、CursorExpired、PlanExpired、PlanStale、UnknownOutcome、ResourceTooLarge、MaintenanceMode。中文文案可以变化，错误码是公开契约。

读取列表默认 limit=50、上限 200；默认返回卡片摘要，详情/资料证据必须显式请求；不默认返回全部图片或长文本。分页按固定排序与唯一 ID 做游标；游标包含查询条件摘要和库版本/epoch。数据变化使快照无法维持时返回 CursorExpired，不能重复/漏项后悄悄继续。

写请求只允许 schema 声明的字段，拒绝未知字段；update 是受限字段 patch，不支持任意 JSONPath 或 SQL。批次修改上限 100 项，每项返回独立结果；默认非原子批次，单项事务，不能把部分成功说成全成功。恢复等操作另有整体事务/维护流程。

## 5. CLI 设计

发布文件 `gamelibrary.exe` 与 Host/MCP/Desktop 同包。以下使用 `gamelibrary` 简写；未配置 PATH 时调用它的绝对路径即可，安装不擅自修改 PATH。

```text
gamelibrary capabilities get --format json
gamelibrary schema get --operation games.update --format json
gamelibrary --data-dir "D:\Official\GameLibrary\LocalData" host status --no-start --format json
gamelibrary scan start --root-id root-demo --expected-data-epoch epoch-demo --idempotency-key scan-001 --format json
gamelibrary jobs get --job-id job-demo --format json
gamelibrary candidates list --state pendingReview --limit 50 --format json
gamelibrary candidates accept --input-file accept.json --format json
gamelibrary games update --input-file update-game.json --format json
gamelibrary launch plan --game-id game-demo --format json
gamelibrary launch execute --plan-id plan-demo --idempotency-key launch-001 --format json
gamelibrary events read --after event-cursor-demo --limit 100 --format json
```

上面的 ID 与文件名均为示例，真实值由前序返回；游戏名称不能充当 ID。显式 `--data-dir` 可由部署配置统一提供，示例省略处不是使用 CWD。`launch execute` 也支持已保存的 profileId + expectedRevision，由宿主先生成并校验内部计划；正常已授权启动不强迫 agent 每次先调用 plan。

写操作 JSON/flags 暴露 libraryInstanceId/expectedDataEpoch 等通用上下文；输入文件示例也必须包含这些字段。携带 planId 的执行可从不可变计划读取实例/epoch，但重试不得自动改成新 epoch。CLI 或 MCP 重连不得通过悄悄刷新旧请求上下文绕过恢复后的状态检查。

- `--format json` 的 stdout 只输出一个结果 JSON；`--format jsonl` 仅用于显式跟随事件/进度模式。日志、提示、诊断写 stderr；不用颜色、动画或欢迎语污染机器输出。
- 默认非交互。参数缺失直接返回错误和 schema/help，不读取控制台等待。`--interactive` 只提供额外人工向导，不拥有更高业务权限。
- 支持 `--input-file <绝对或调用方明确相对路径>` 和 `--input -` 从 stdin 读取 UTF-8 JSON；参数文件相对路径仅用于 CLI 读文件，文件内业务路径须为绝对或明确 relativeTo。禁止混用冲突的 JSON/flag 字段；重复字段报错。
- JSON 中 argv 是字符串数组。复杂路径、简介和批量更新优先输入文件，不要求 agent 拼接 PowerShell 引号。
- `--help` 输出人类说明，schema 命令输出 machine-readable 契约。`--timeout` 限制等待，不默认撤销已受理作业。
- 可预览的变更提供 `--dry-run`/对应 plan 操作；结果含拟改字段、目标版本与影响。正常单条编辑不强制两阶段确认。
- `--yes` 最多代表省略 CLI 本地交互，不能授予宿主权限、绕过工具验证或修改扫描只读边界。

退出码固定：0=完成或作业已受理（仍需检查 status）；2=参数/schema 错；3=对象不存在；4=Revision/幂等冲突；5=权限不足；6=需要用户或外部工具处理；7=宿主/版本/连接不可用；8=业务失败；9=等待超时/等待被取消；10=结果不确定；11=部分完成。JSON 业务码比数字退出码更具体。

## 6. MCP Server 设计

使用官方 `ModelContextProtocol` C# SDK 实现原生 stdio 服务；开工时验证并锁定稳定包版本。按协议协商版本与能力，不手写 JSON-RPC 解析器。C# SDK 提供 stdio 服务与类型化工具注册。[官方 C# SDK 入门](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md)。

### 6.1 接入配置

```json
{
  "mcpServers": {
    "gamelibrary": {
      "command": "D:\\Official\\GameLibrary\\artifacts\\publish\\win-x64\\GameLibrary.Mcp.exe",
      "args": ["--stdio", "--data-dir", "D:\\Official\\GameLibrary\\LocalData"]
    }
  }
}
```

这是常见客户端配置示意，客户端顶层格式以其要求为准。路径在 S6 发布后才存在；首次接入向导显示最终路径，不能误称配置复制后现在即可运行。注册 MCP 客户端配置需按用户选择修改目标客户端文件，不自动修改所有客户端配置。

### 6.2 Tools 与结果

- tools/list 输出 catalog 的操作名、用途、inputSchema、outputSchema 与能力注解；tools/call 直接调用 HostClient。工具参数要有明确枚举、默认值与路径语义，不使用只有 `action:string,payload:any` 的万能执行器。
- 对结构化结果启用 SDK 的结构化输出支持；`structuredContent` 返回第 4 节的 envelope，并提供兼容的 JSON 文本内容。outputSchema 覆盖成功、accepted、partial、needsUserAction 和业务错误结果。
- readOnlyHint/idempotentHint/destructiveHint/openWorldHint 按真实行为设置。写资料、启动游戏不能标只读；只有相同幂等键约束的行为可声称请求重放安全。工具提示不替代 Host 权限校验。
- 业务失败/partial/needsUserAction/unknownOutcome 放入正常 CallToolResult 并设 isError=true，附可解析错误或行动信息；不能把描述文字当成功值。协议/schema 错误使用 SDK 的协议错误机制，不伪装成游戏错误。
- stdout 只承载 MCP 协议；所有日志到 stderr/日志文件。除非客户端协商支持，不发送额外 MCP logging/resources subscription 等消息。
- 只实现已验证并协商的协议能力；本项目的长任务用 Job API，不依赖客户端实现某个可选/实验性 MCP Tasks 扩展。

上述 Tools schema、结构化结果与注解语义依据 [MCP Tools 规范](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)；错误映射参考 [C# SDK Tools 文档](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md)。协议版本与应用 API v1 分别管理。

### 6.3 Resources 与数据可读性

实现只读资源模板：`gamelibrary://capabilities`、`gamelibrary://games/{gameId}`、`gamelibrary://jobs/{jobId}`、`gamelibrary://assets/{assetId}/thumbnail`、`gamelibrary://notifications/{notificationId}`。同一数据同时有对应 Tool 查询，使不支持资源模板的客户端仍能操作。

URI 只接受注册的类型与 ID，不拼接任意文件路径；校验库实例、资产归属、输入大小和权限。资产预览不超过 1 MiB；较大数据返回分页/明确资源引用。业务内容含 sourceKind/evidence；README、目录名、简介中的文字不得进入工具描述或成为可执行指令。

Resource subscription 是增强能力：支持时推送资源更新信号，客户端再读取；不支持时使用 events.read。通知本身不是可靠投递存储，必须可从持久事件/实体状态补齐。

## 7. 并发、重试与副作用

### 7.1 资料变更

更新/删除带 expectedRevision；宿主在事务中比较，不一致返回 RevisionConflict/currentRevision。客户端重新读取、决定合并，不隐式 last-write-wins。创建/接受候选、作业启动等无既有 Revision 的请求使用幂等键与业务唯一约束。

所有有副作用操作使用 idempotencyKey。收据按 `(libraryInstanceId, actor, operationId, key)` 唯一；保存规范化请求摘要、结果或 JobId。相同键不同请求返回 IdempotencyConflict；同请求重试返回原收据，不重复执行。并发相同键先争取唯一收据，再执行业务。

常规数据库变更与收据同一事务提交；Job 入队与收据一起提交。保留活动作业收据直到最终状态，并至少保留完成后的 30 天；启动收据建议 90 天。超过期限不得承诺重放安全，客户端生成新意图时才用新键。

### 7.2 游戏启动不是 SQLite 事务

启动过程：先持久化 Prepared/Executing 收据与 LaunchAttempt → 释放事务 → 启动外部进程 → 记录 ProcessCreated/观察结果。任何时刻都不能持有 DB 锁等待游戏退出。

进程已创建但收据尚未提交时 Host 可能崩溃；这里不可能仅靠 SQLite 保证 exactly-once。恢复时根据已记录 PID+启动时间+路径做尽力核实，无法证明时标 UnknownOutcome，原幂等键重试不得再次启动。返回已知信息与“检查运行状态/显式新启动”的恢复操作。禁止通过自动换键、重复试 CLI 参数规避该状态。

一个游戏的一次启动互斥作用于所有入口，不是各客户端独立去抖。MTool 第二步失败时保存首步结果，不重新注入；相同键查询同一尝试。权限过期、Profile 修改、工具指纹变化使旧 LaunchPlan 失效。

## 8. 作业、事件、取消与恢复

扫描、图片重建、大导入、备份等立即返回 accepted + JobId。Job 状态固定为 queued、running、paused、cancelRequested、cancelled、succeeded、failed、needsUserAction、interrupted、unknownOutcome。进度可未知；不能靠猜测总目录数生成 99% 永不结束。

- `jobs.wait` 最长等待 30 秒，返回结果或当前状态与 nextCursor；客户端自行决定再等。协议请求取消只中断该次等待，作业取消必须显式 jobs.cancel/scan.cancel。
- cancel 返回 cancelRequested，不谎称已取消；扫描在检查点结束。进入恢复提交临界区或其他不可中断步骤时返回 CannotCancelNow，说明当前阶段。
- CLI 退出/MCP 断开后，已受理作业继续；持久 jobId 可以重连查询。进程内 CancellationToken 不等于可靠跨进程作业取消。
- 扫描按已完成分支重新验证并续扫；查询可重试；外部启动/注入不自动重放。备份恢复重启依据维护日志恢复/回滚，不把所有 running 作业一律恢复执行。
- 事件与实体变更在同一数据库事务持久化，事件序号单调递增；客户端游标持久保存，支持重连回放。事件只传 ID、Revision、摘要和类型，默认不传长简介/图片。
- 事件日志初期保留 7 天或 100,000 条，以先触达上限为准；旧游标返回 CursorExpired 与当前基准。客户端重取列表再建立新游标，不把无法回放当没有变化。
- 恢复备份会创建新 dataEpoch，令旧 Revision 语境、游标、计划与会话缓存失效。避免老游标从恢复后较小序号继续读取造成漏事件。

## 9. 授权与可持续自动操作

在本地用户明确接入时选择/配置 agent 的授权范围。正常库查询、编辑、入库、配置、已允许的游戏启动可以持续执行；不要求每次再打开桌面批准。是否允许游戏启动、工具验证、导出到外部目录、备份覆盖恢复、系统开机启动由宿主授权策略决定。

权限集合建议：library.read、library.write、scan.manage、launch.execute、tools.configure、tools.verify、settings.write、files.import、files.export、backup.create、backup.restore、host.manage、access.admin。此集合是应用权限，不是 OS 权限，也不会改变用户已有的游戏盘边界。

计划与批准规则：

1. 常规资料编辑可直接使用 expectedRevision/idempotencyKey，无需两阶段交互。
2. 覆盖恢复、库导入冲突覆盖、范围显著扩大的根绑定等先生成影响计划，包含目标、库/实体版本、摘要、有效期（建议 10 分钟）。
3. 用户已有授权明确覆盖该操作时，agent 可以原生执行计划；没有授权才返回 NeedsAuthorization/actionId/scope，不无限等待 GUI。
4. 完整上下文来自 Host 的持久授权与操作计划。`confirmed:true`、`--yes`、actor="owner"、工具描述里“已批准”都不是授权凭证。
5. 授权授予/撤销本身也有 CLI/MCP 操作，但要求现有 owner/admin 权限；受限 agent 不可用 access.configure 给自己升权。首次 owner 引导使用本地用户入口或宿主外已核实的接入机制；本项目不声称能认证模型转述的用户原话。
6. 审计记录 actor、入口、operationId、目标 ID、计划/授权引用、幂等键、结果与可逆变更摘要。用户可查询和撤销授权；不记录工具登录信息或 token。

本机接口不开放任意 shell、SQL、注册表写入、任意文件删除或远程 HTTP 代理。输入路径只服务于明确业务操作，导入/导出校验授权范围与最终路径，防止 `..`、重解析点或 URI 将资产访问转换为任意文件读取。

## 10. 备份恢复与维护模式

备份 create 是 Host 长任务，使用一致性 SQLite 备份及用户资产清单，不能让 CLI 直接复制 WAL 数据库。导出目录/源路径显式指定并校验授权，默认生成应用自己的产物 ID。

导入/恢复先将归档作为不可信数据解析，限制条目数、压缩后/解压后体积、路径长度和嵌套；拒绝绝对路径、`..`、重解析点与路径穿越，只解包至专用暂存目录。执行文件、外部游戏和客户端接入密钥不属于库备份内容。访问授权保留当前目标 Host 的控制区策略，不从导入备份自动恢复/扩大权限；源库路径与工具绑定先校验再生效。

restore_plan 检查备份 schema、完整性、目标库、资产与覆盖影响；restore 执行前验证 planId、dataEpoch、授权、备份摘要和幂等键。先备份当前状态；进入维护模式拒绝新业务写入，暂停扫描、排空连接，再恢复到暂存目录验证。

真正切换由维护日志记录前后目录/文件状态；恢复过程中崩溃，下次 Host 按日志保留旧库或完成切换，不能以覆盖单个 db 文件完成恢复。恢复完更新 dataEpoch，关闭旧客户端订阅并要求重新握手，旧计划不再有效。

恢复请求的控制收据与维护日志存放在 LocalData 的控制区，不随被恢复的业务库回滚；否则重试可能找不到原收据再次覆盖。控制区不是第二个业务数据库：首版可使用长度受限、Flush 落盘的追加日志及明确恢复状态机，并用崩溃测试验证。

恢复时目标 libraryInstanceId 保留，生成全新 dataEpoch，备份源实例仅记录为 provenance。restore 请求凭控制收据可以在原连接断开后查询同一恢复结果；其他旧 epoch 的变更一律拒绝。客户端重新握手/读取新版本，不得把旧启动请求自动换新 epoch 再次提交。

## 11. 无 GUI 的原生使用流程

一个 agent 的完整流程应只依赖接口：

1. capabilities.get 确认 API、权限、库实例、工具能力。
2. roots.list 取 RootId；scan.start 返回 JobId；jobs.wait 与 scan.coverage 获取结果。
3. candidates.list/get 查看引擎、入口和资料来源；用明确 CandidateId + Revision 调用 accept/defer/ignore。
4. games.get/fields.set/tags.assign/assets.import/choose 完成资料编辑；UI 未运行也可完成。
5. profiles.create/update/set_default、translation.set、tools.register 完成启动配置；verification.start/get 获取真实适配证据。
6. launch.plan 查看工具/cwd/argv/验证结果；授权已覆盖则 launch.execute；否则读取具体 nextActions。
7. launch.status/jobs.get 区分已发送、已观察、待外部操作和失败；不能把工具窗口出现作为成功。
8. notifications.acknowledge 处理已读；backups.create 保存库；desktop.show/navigate 可在用户需要时展示同一结果。

第三方 Guided 状态返回 actionId、gameId、toolId、reason、requiredStep、相关资源和允许的 continuation。actions.complete 只提交用户观察/产生物 ID，必须重新验证；不接受 agent 自填 success=true 就将翻译兼容性升级为 VerifiedAutomatic。

## 12. 开发顺序与硬验收

S0：Contracts、最小 Host/HostClient、CLI ping、MCP initialize/tools/list/tool-call、stdio 纯净性测试。S1：扫描 Job 与候选查询同时交付 CLI/MCP。S3：无 GUI 完整纵切，先用测试桩覆盖启动。S4–S5：每个资料/设置/通知功能随 UI 同步提供 API。S6：全量 catalog 覆盖与真实 stdio 客户端验收。

不能一次搭完所有工具再补业务。每一小步只实现一个 handler 加两个薄接口适配，并共享原有测试。对暂未实现的 catalog 预定项记录 Planned；tools/list 不能把它们当可用工具。

必须执行：

- 同一 fixture 分别通过 CLI、MCP、桌面用例写入，在统一查询中得到等价状态。
- 至少一个不启动 Desktop 的从 library.init 到启动桩和备份恢复的 E2E。
- 两客户端并发/断连重试/宿主冷启动竞争/崩溃恢复，不丢资料、不重复启动。
- 真实 MCP 客户端通过 stdio initialize、tools/list、tools/call、resources/read 验证 schema；不要只单测 C# 方法就宣称 MCP 可用。
- CLI stdin/file 输入、退出码、stderr、JSON/JSONL 输出与路径转义契约测试。
- 每个产品 operation 均有 CLI/MCP 映射、权限与错误说明；catalog 差异门禁失败时不能发布。
- 恢复期间控制收据不丢、dataEpoch 改变、旧计划与游标拒绝。
- RenpyThief 不确定能力仍保持 Guided/Unknown；原生接口不会绕过原策划的翻译验证闸门。

本修订只新增开发规格，未安装 SDK/插件、注册 MCP 客户端、启动宿主或操作真实游戏。工程实现与运行验收应按任务书逐项推进。
