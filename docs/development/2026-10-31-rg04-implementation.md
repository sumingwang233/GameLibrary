# RG-04 实施记录：作业结果、封面队列与增量刷新

2026-10-31。按用户“那就继续”的授权实施 [RG-04 计划](2026-10-31-rg04-plan.md)，沿用 v1.8.2 草稿和隔离分支 `codex/optimize-footprint-v1.8.1`。本轮由 Codex 串行写入，没有向其他服务发送源码。

工作目录：`C:\Users\sumingwang\.codex\worktrees\optimize-footprint-v181\GameLibrary`。没有改动 main、正式游戏库或正式实例，也没有安装依赖、推送实现提交、打包或发布。

## 本地源码检查点

| 任务 | 提交 | 实现 |
| --- | --- | --- |
| J1 | `5f67384` | JobManager 默认保留最近 128 个可淘汰终态对象；更旧结果转存到会话临时文件，保留查询 |
| C1 | `20db994` | 封面数据请求并发上限为 2；同一资产共享请求，按消费者取消排队工作 |
| Q1 | `75d203a` | 小批、已知游戏变更优先读取单条；不能证明成员和排序不变时保留完整查询 |

这些提交是实现检查点，尚未通过统一验收。前轮虚拟行、隐藏刷新合并、延迟模块、源生成 JSON 和 NativeAOT 候选仍见 [RG-03 实施记录](2026-10-31-rg03-implementation.md)。

## 实现边界

作业结果：`CompletedJobHistory` 用一个独占、关闭即删除的随机会话文件保存 JSON 结果，内存只留 jobId 到文件位置的索引。不重新打开旧文件、不删除既有文件、不增加数据库表或迁移。旧结果回读为 JSON，公开响应形状保持；近期对象保留原类型。Host 等待作业停止后关闭该文件。

转存成功后才移除原对象。转存失败时保留原对象并计数，终态记录仍待重试的作业也不淘汰；因此 128 是正常转存时的近期终态目标，不是总作业内存硬上限。运行中作业、待记录重试对象、每个历史作业的轻量索引及回读缓冲仍占内存。该方案以会话临时磁盘和查询 I/O 换取旧结果对象释放；索引和临时文件随作业数增长，尚无磁盘或内存收益实测。

封面队列：沿用 64 MiB 字符串缓存预算，最多启动两个 fetch。卡片屏外、卸载或详情读取被替代时只取消自己的消费者；最后一个消费者离开后移除尚未启动的任务。已发出的 IPC 仍完成并占用并发名额，避免误取消共享调用。库或资产失效后，迟到成功结果不得写回缓存；已经在途的原始传输/纪元错误继续返回。预算不包含解码图像、队列外的 WebView 数据或整个进程占用。

增量刷新：事件层最多合并 16 个已知 `game.updated` ID，控制器最多保存 16 轮失效记录。未知事件、记录断档或超限均标为完整失效。只有变更条数少于已加载页数、条目已加载、查询没有额外筛选，且库身份、数据纪元、活动成员、played 条件、实际 SQL 排序键和 revision 均安全时，才替换对应对象并保留分页游标。搜索、标签、收藏筛选、封面筛选、视图及不确定排序均完整查询；显式 reload 也完整查询。

不同库/纪元的分页响应不能拼接，旧纪元的较高 revision 也不能覆盖新纪元结果。隐藏期间的事件消费、合并刷新和游戏退出唤醒逻辑保留。Host 公共 IPC、SQLite schema、跨层字符串和发布配置均未因 RG-04 改变。

## 已准备的回归源码

| 文件 | 新增覆盖意图 |
| --- | --- |
| JobManagerTests.cs | 容量超限后查询中文结果、近期对象身份、转存失败保留结果、会话文件关闭删除 |
| assetCache.test.ts | 并发上限、最后消费者取消后跳过排队请求、共享请求单消费者取消、清理队列并保留在途错误 |
| gameChanges.test.ts | 连续失效历史、未知变更/断档回退、排序键、played/成员变化和旧 revision |
| hooks.test.ts | 已知 ID 合并与未知失效保留、已加载多页的局部刷新/完整重查、跨纪元较低 revision 恢复 |

本轮只做源码静态复核和结构索引同步。没有运行测试、构建、lint、类型检查、桌面启动或性能采样；表中用例不能记为通过，旧 RG-02 样本也不能证明 RG-03/04 收益。

## 统一验收增量清单

继续使用 [RG-03 的完整验收次序](2026-10-31-rg03-implementation.md#统一验收次序)。以下只是待执行入口；执行时必须把 TEMP/TMP、合成库、日志、解压缓存和 WebView 数据限定在本 worktree 的 `.tmp/rg04/`，桌面使用独立测试 identifier。只使用已有依赖，缺失时停止。

在前端目录的原定用例列表中增加 `src/lib/hooks/gameChanges.test.ts`；`assetCache.test.ts`、`hooks.test.ts`、详情和卡片用例继续运行。可单独定位本轮新增逻辑：

```powershell
npm.cmd test -- src/lib/assetCache.test.ts src/lib/hooks/gameChanges.test.ts src/lib/hooks/hooks.test.ts src/components/GameCard.test.tsx src/components/DetailSheet.test.tsx src/components/DetailSheet.gameProp.test.tsx
```

在仓库根增加作业专项检查，覆盖新容量逻辑及已有记录重试用例：

```powershell
dotnet test tests/GameLibrary.IntegrationTests/GameLibrary.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~JobManagerTests' --results-directory .tmp/rg04/test-results
```

统一功能验收还须检查：终态持久化重试与取消竞态；超过 128 个完成作业后的历史查询及 Host 退出清理；封面快速滚动、共享消费者、修改/切库期间的迟到结果；默认及其他排序、搜索/标签/收藏/played/封面/视图筛选、触底和详情修改后的滚动；隐藏后积累未知事件、库切换及恢复后的列表一致性。

性能对比继续使用相同合成库、Release 设置与预热方式，记录整棵桌面进程树、启动耗时和文件字节数。另比较连续作业前后内存、会话临时文件增长与历史查询耗时，以及封面加载/刷新次数。NativeAOT 仍是显式候选，必须试编并对实际产物回归后才能考虑切换默认。

## 图谱与交付状态

源码检查点 `75d203a9065992792ca50d3a2b059e4250c0ba8b` 的图谱使用本 worktree 作为显式 repo_root，minimal 后处理，无嵌入模型。解析无错误，索引 441 个文件、4,321 个节点、43,356 条关系；CALLS 直接解析 9,491 条、未解析 24,660 条。见 [RG-04 快照](../code-review-graph/rg04-footprint.json)。这些是结构数据，不是编译或功能证据。

J1、C1、Q1 的实现与回归源码已提交，D1 文档和图谱同步完成；运行正确性、性能收益、NativeAOT 默认切换及整体验收仍待执行。不将源码完成等同于验收完成。

用户随后授权生成本机验收安装包；v1.8.2 的生产编译、NSIS 打包及未运行项目另见 [本地验收构建记录](2026-10-31-v1.8.2-acceptance-build.md)。这不改变本轮原始未执行记录，也不代表实机验收通过。
