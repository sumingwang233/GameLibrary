# GameLibrary 开发 agent 任务书 v1.1

工程根目录：`D:\Official\GameLibrary`；日常数据：其 `LocalData` 子目录。

必须配套阅读：[主策划案](<D:/Official/GameLibrary/开发策划案 v1.md>)、[MCP 与 CLI 契约](<D:/Official/GameLibrary/MCP与CLI原生接口契约.md>)、[领域与可靠性补充规格](<D:/Official/GameLibrary/领域与可靠性补充规格.md>)。用户提供的修正建议保留为参考，采用与调整结果以补充规格第 7 节为准。本任务书不授权修改游戏盘文件。

## 开工提示词

```text
你要实现 Windows 本地游戏库 GameLibrary。
先完整阅读 D:\Official\GameLibrary 下主策划案、MCP/CLI 契约、领域与可靠性补充规格，再检查 AGENTS.md。
工程使用 D:\Official\GameLibrary；现有文档和 .obsidian 不覆盖，不另建 Local 前缀工程。
F:\ 是用户游戏数据盘，只读扫描；不得整理、搬移、重命名、删除游戏。
日常数据保存到 D:\Official\GameLibrary\LocalData；测试仅用独立 artifacts/test-runs/<guid>/data。
技术栈固定 C#、.NET 10 LTS、WPF/XAML、Microsoft.Data.Sqlite。
原生 GUI、CLI、MCP 同级；唯一 Host 管理业务/数据库，三入口都经 HostClient。
先按 S0 完成 Contracts/Host/CLI/MCP 最小骨架与协议测试，再按任务依赖推进。
GameId 是永久 GUID；路径与匹配指纹不是身份；候选审核、物理类型、可用性分开。
每新增一个业务用例就同步实现 CLI/MCP，不留到开发末期，也不由 MCP 调 CLI 文本输出。
每次选择一个小任务，先写契约、状态转换和验收，再实现一个 handler 与入口适配。
按相关规则运行 build/format/tests；性能、恢复和真实工具测试只按实际执行结果报告。
有副作用操作使用持久幂等收据；并发编辑校验 Revision，外部启动不宣称通用 exactly-once。
MTool/RenpyThief 的协议结论必须区分静态证据与实际验证。
不猜 RenpyThief 参数，不执行扫描到的脚本，不以打开工具窗口冒充翻译启动成功。
不得访问翻译工具账号文件或自动发起付费翻译。真实游戏启动/注入验证使用用户指定隔离样本。
如已有部分工程，先核对进度和测试，再继续下一小步，不重建整个项目。
```

## 任务列表

每项完成后，在项目 `docs/progress.md` 中登记：任务 ID、改动文件、验证结果、未验证范围、下一项。此表只规定交付内容，不授权自动创建远程仓库或推送代码。

| ID | 前置 | 文件/模块职责 | 交付与验收 |
|---|---|---|---|
| T00 | 无 | 工程基线 | SDK/包版本、配置与日志骨架、分析器、测试工程、空窗口、首份构建报告；不修改用户环境变量 |
| T01 | T20 | Domain 路径与身份 | GUID/比较键/指纹分离；长路径/非法输入/大小写/UNC 策略；exFAT 不依赖 NTFS ID |
| T02 | T01,T21 | Filesystem | 分段/取消/暂停、安全游标、规则优先级和覆盖字段；异常与预算延后可解释 |
| T03 | T02 | 检测器 | 五类首批引擎/格式；正负/不可读证据、规则版本、置信度、确定冲突消解 |
| T04 | T03 | 边界与分类 | 正交 CandidateKind/ReviewState/Availability；toolNeed 覆盖；合法/循环/失效 LNK |
| T05 | T04,T22,T23 | 原生扫描接口 | scan start/inspect/coverage、候选查询和 Job 状态同步提供 CLI/MCP；替代 Probe |
| T06 | T20,T21 | 启动计划/执行器与桩 | argv/cwd、全入口互斥、步骤/交接/观察状态；CLI/MCP 通过同一执行器，真实工具不自启动 |
| T07 | T05,T06 | MToolAdapter | 静态/生成物/CLI/实测证据区分；最小 BAT 解析；旧路径修复；声明副作用、无沙箱假承诺 |
| T08 | T06 | RenpyThiefAdapter/验证记录 | 权限与技术能力分开；Unknown/Guided/SemiAutomatic/VerifiedAutomatic；样本、失败和恢复证据 |
| T09 | T06 | Player/SteamAdapter | 参数模板、缺播放器/Steam 客户端/清单、离线错误；不猜 appid 或悄悄直接启动 |
| T10 | T01,T20,T21 | Persistence 基础 | 独占 Host、WAL/外键/Revision/schema 版本、事务迁移、备份 API；损坏不建空库 |
| T11 | T05,T10,T23 | 入库/忽略 | 候选状态机、accept 幂等、defer、ExactPath/Subtree/ConfirmedIdentity 抑制与撤销 |
| T12 | T06,T11 | Desktop 纵切 | 10–20 卡片；仅用 HostClient；与 CLI/MCP 同库；异步状态与错误、重启保留 |
| T13 | T07,T08,T12 | 翻译配置三入口 | 工具/Profile/策略读写、校验与原生计划；Required 不回退；能力/授权/观察分开 |
| T14 | T11 | Metadata/Assets | 来源、明确接受的建议、Auto/Set/Clear、标签 Suppress、图片导入与裁切；三入口覆盖 |
| T15 | T12,T14,T24 | 游戏库 UI/视图接口 | 编辑、搜索防抖、收藏、可见行虚拟化、图像取消/LRU、DPI/键盘；状态可由 API 控制 |
| T16 | T02,T11,T23 | ScanCoordinator | 队列上限 4096、事件风暴折叠、稳定观察、周期核对、手动/后台互斥 |
| T17 | T16 | Reconcile/Identity | 重关联仅改 DB；副本/备份是提示；offline/accessError/suspectedMissing/missing 分开 |
| T18 | T16,T15 | 通知/托盘 | ack 不等于接受候选；退出 Desktop 与停止 Host 区分；全屏、未保存编辑、可选开机启动 |
| T19 | T30 | 发布与文档 | 全盘只读覆盖、样本结果、四组件自包含 ZIP、客户端配置、升级/恢复与已知限制 |
| T20 | T00 | 架构契约/ADR | 核心 ER/状态机、operation catalog/schema、适配器能力、7 份短 ADR；接口负责人和版本 |
| T21 | T20 | 配置/Host/HostClient | DataDirectory 初始化、同用户管道、规范路径独占锁、握手/重连、冷启动竞争；不读写日常库测试 |
| T22 | T21 | CLI/MCP 原生骨架 | capabilities/schema/status、JSON/退出码、stdio 协议、Tools/Resources；SDK 固定版本 |
| T23 | T10,T22,T06 | 共享执行语义 | 请求收据、Revision、Job/取消/事件、权限与计划；断连/重复/外部执行歧义测试 |
| T24 | T21,T10 | 可观测性/诊断 | 日志/审计字段、脱敏/轮转、队列/耗时指标、诊断 UI/CLI/MCP 与版本查询 |
| T25 | T05,T16,T24 | 扫描性能基线 | 10 万实文件夹具、事件风暴、取消 P95 与内存报告；固定环境与回归阈值 |
| T26 | T15,T24 | UI/检索性能基线 | 5000 条库、200 查询、冷热首屏、DPI/滚动/缓存报告；PERF-04/05/06 |
| T27 | T10,T23,T24 | 故障恢复 | 配置/DB/缓存损坏、迁移/恢复/启动注入故障、控制收据、Epoch 失效；REC-01 至 REC-05 |
| T28 | T09,T13,T14,T17,T18,T24,T27 | 三入口覆盖审计 | 对已逐步实现的 catalog 补缺；备份/配置/权限/视图也覆盖；不是最后才开发 MCP |
| T29 | T28,T27 | 协议与边界检查 | 真实 stdio 客户端、资源越界、参数/LNK/路径攻击、授权与第三方副作用、隐私日志 |
| T30 | T25,T26,T29 | 发布前统一验收 | 无 GUI 全流程、三入口一致性、旧契约兼容、性能/恢复报告与 schema 覆盖门禁 |

T06 等任务可在顺序上调整，但一个 agent 同时只推进一个验收单元。若未来明确安排多 agent，由负责人按模块分配文件所有权，禁止互相回滚；数据库 schema、接口和共享模型由单一负责人维护。这里不要求为了执行任务主动派生子 agent。

编号保留方便追踪，不是执行顺序。起步按 T00 → T20 → T01 → T21，之后依赖就绪再做；T19 是最终发布，不能按数字在 T20 之前执行。T10/T23/T27 等大项须按“一个状态转换或故障点 + 一条验收”拆成连续小提交，不能一次堆完。每项都同步实现属于它的 CLI/MCP 操作，T28 只检查覆盖。

共享契约变更先登记版本/ADR/消费方影响再修改；已明确分工时不编辑其他 agent 所有权文件，发现共享缺陷先登记并交负责人处理。

## 必须先建立的人工夹具

```text
fixtures/generated/<test-id>/
  [ANIM]/ExampleA/Game.exe + data.xp3
  [26.7.27]/[Clockup]/ExampleB/Game.exe + data.xp3
  [toolNeed]/[Unity]/ExampleC/ExampleC.exe + ExampleC_Data/ + UnityPlayer.dll
  [RPG]/ExampleD/Game.exe + www/js/rpg_core.js + www/data/System.json
  RenpyExample/Example.exe + renpy/ + game/options.rpy
  Collection/One/(独立引擎样本)
  Collection/Two/(独立引擎样本)
  FlashCollection/a.swf + b.swf
  Tools/MTool.exe
  BrokenShortcut.lnk
  单文件 100%.exe
```

空文件仅能用于存在性检测；PE 使用项目自己的 x86/x64 编译桩和受控头样本；SWF 使用合法最小样本与损坏样本；LNK 用 Windows 接口生成合法/失效/循环测试文件，不拿伪结构证明解析成功。生成器、固定内容 hash 和时间控制纳入版本控制。中文/日文/`&`/`%` 路径、大小写、重复入口、嵌套、复制中状态动态扩展；双引号文件名等非法输入作为拒绝测试。

所有生成/清理使用唯一 test-id，只在验证过的测试根内执行。报告写入工程 artifacts；不调用 F 盘旧测试文件，因为旧测试语义包含搬移游戏目录。

## 翻译适配验证记录模板

每一个适配器结论应填写以下字段，缺一项不能标为完整自动接入：

```text
工具：
本地入口与工作目录：
显示版本 / 关键文件指纹：
适配器版本：
证据来源（官方说明 / 生成脚本 / 生成快捷方式 / 实测）：
用户允许的隔离样本：
游戏引擎与位数：
初始状态（未部署 / 已部署；工具未运行 / 已运行）：
调用计划（可执行文件、参数数组、cwd、顺序）：
游戏是否启动：
工具是否关联正确游戏：
翻译是否实际生效：
是否需要人工步骤：
新增/改变的文件范围：
失败类型及恢复方法：
结论（VerifiedAutomatic / SemiAutomatic / Guided / Unsupported / Unknown）：
授权状态与适用范围：
外部副作用声明（已观察/可能/未知写入、联网、回滚证据）：
结论适用范围、日期、验证人：
```

MTool 先核实本地配方，再试 CLI。RenpyThief 先官方拖入隔离样本，再检查生成物；`RenpyThiefLauncher.exe` 的存在不能代替参数协议证据。不要读取账户与 token 文件寻找“接口”。

## 阶段报告格式

每次交接只需要说明：

1. 已完成哪些任务、用户现在可以实际做什么。
2. 哪些文件修改、为什么修改。
3. 已运行的构建/格式/测试、契约/无 GUI 测试及结果；性能与真实集成是否运行。
4. 未验证与阻塞项，特别是翻译自动化能力。
5. 下一项任务 ID 和进入条件。

禁止写“后端基本完成”“理论上支持所有引擎”这类无法验收的结论。自动识别结果要有来源；启动结果要有观察；测试结果要能复现。

## 发布前核对清单

- [ ] 仓库位于独立开发目录；不把 F 盘当 Git 工作树。
- [ ] 用户资料保存在应用目录；扫描不向游戏目录写文件。
- [ ] 真实 F 盘扫描覆盖明细能解释未识别、未扫描、被排除与错误分支。
- [ ] toolNeed 多层继承与翻译启动已按样本记录，所有 Guided 项明确展示。
- [ ] MTool 旧路径、RenpyThief 更新、坏快捷方式均有修复入口。
- [ ] 已有游戏和新游戏分开审核，忽略/稍后/重启不产生重复提醒。
- [ ] exFAT 不使用 NTFS-only 身份方案；离线不删除用户游戏资料。
- [ ] 用户改名、简介清空、自动标签删除、封面替换在重扫后保留。
- [ ] build/format/tests 通过，未执行的真实测试明确列出。
- [ ] 备份恢复和数据库兼容策略验证；发布包不含真实游戏、账号资料和商业工具。
- [ ] 自包含包可在没有 SDK 的机器运行；没有把 SDK 缺失作为终端用户安装要求。
- [ ] 若 RenpyThief 全自动仍无可靠接口，不宣称该需求已经完成。
- [ ] 每个产品操作有原生 CLI/MCP 与 schema；没有仅桌面可用功能，也没有任意 Shell/SQL 后门。
- [ ] 三入口连接同一 Host 与 DataDirectory；用户日常 LocalData 不被 CI 使用。
- [ ] 断连重试、并发编辑、外部进程崩溃歧义与作业恢复有可复现测试。
- [ ] 领域正交状态、忽略范围、规则优先级、负向/不可读证据按补充规格实现。
- [ ] 10 万文件/5000 卡片/200 查询/事件风暴有环境、P95、内存与回归报告。
- [ ] 配置/数据库/缓存损坏、升级中断、备份恢复切换均有故障演练。
- [ ] 恢复控制收据不随业务库回滚；新 dataEpoch 使旧游标和计划失效。
- [ ] 副作用声明未冒充沙箱；翻译成功未用“窗口打开/进程存在”替代文本生效证据。
- [ ] 发布包包含 Host/Desktop/gamelibrary CLI/MCP 与可复制配置；真实 stdio 客户端可原生操作。
