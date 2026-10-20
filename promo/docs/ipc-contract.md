# GameLibrary 前端 · IPC 契约 / 启动时序 / DOM 事实

> 目的：在纯浏览器里用 mock 后端跑真实前端，并放进可缩放"相机"层逐帧渲染。
> 只描述事实与结构。所有锚点相对仓库根；行号为当前工作副本实测。
> 前端根：`src/GameLibrary.Tauri`；传输层真源：`src/GameLibrary.Tauri/src/lib/api.ts`；Rust 壳：`src-tauri/src/lib.rs`。

---

## 0 传输形态（一句话）

前端只认一个 Tauri 命令 `bridge_request`，它把一条 JSON 行喂给 sidecar `GameLibrary.TauriBridge.exe` 的 stdin，读 stdout 一行回来（`src-tauri/src/lib.rs:231-264`、`:88-109`）。所有业务 operation 都走这条管道，`operationId` 是字符串，`parameters` 是对象，响应是 Envelope。

| 层 | 位置 | 事实 |
| --- | --- | --- |
| 前端封装 | `src/lib/api.ts:72-125` | `operation<T>(operationId, parameters?, intent?)`；`requestId = tauri-<Date.now()>-<counter>`；`payload = {...parameters, idempotencyKey?}` |
| Tauri 命令名 | `src/lib/api.ts:86` | `invoke("bridge_request", { request: { requestId, operationId, parameters } })` |
| 取用方式 | `src/lib/api.ts:83` | `window.__TAURI__?.core?.invoke ?? invoke`（`withGlobalTauri: true`，`tauri.conf.json:12`） |
| 超时 | `src-tauri/src/lib.rs:63` | 120 s，超时即杀 bridge 进程、下条请求重启 |
| 双通道 | `src-tauri/src/lib.rs:237-241` | `operationId === "events.wait"` 独享一条进程（长轮询不堵业务请求） |
| 错误码 | `src/lib/api.ts:110-116` | 非 2xx → `OperationError(code, message, envelope)`；`describeFailure` 把 `nextActions[].reason` 拼进文案（`:157-168`） |
| 传输失败 | `src/lib/api.ts:86-95` | 抛 `TransportError`，幂等键保留（未 release），可重试命中后端收据 |

Envelope（`src/lib/types.ts:7-19`）：

```json
{ "apiVersion": "string?", "requestId": "string", "libraryInstanceId": "string?",
  "dataEpoch": "string?", "ok": true, "status": "string", "data": {},
  "jobId": "string?", "error": { "code": "", "message": "", "retryable": false, "recoveryOperation": null },
  "warnings": [], "nextActions": [ { "operationId": "", "actionId": null, "reason": "" } ] }
```

`api.ts:96-105`：`libraryInstanceId + dataEpoch` 同时出现时视为库身份；变化 → 清空封面缓存（`invalidateAssets`）。mock 里两者要么都给、要么都不给，别中途变。

---

## 1 启动时序

### 1.1 挂载链

`index.html:11-13` → `#root` → `src/main.tsx:9-19`：`ErrorBoundary` → `SettingsProvider`（`lib/settings.tsx`）→ `LibraryProvider`（`lib/state.ts`）→ `App`。`App` 内 `AppShell` 包 `TitleBar` + `Sidebar` + `<main>`（`App.tsx:240-241`、`components/AppShell.tsx:5`）。

### 1.2 启动即发的调用（按实际发生顺序）

| # | operation | 触发点 | 请求 | 前端期望 |
| --- | --- | --- | --- | --- |
| 1 | `settings.get` | `lib/settings.tsx:66`（provider effect，与 #2 并行） | `{}` | `LibrarySettings`（`types.ts:216-228`），`revision` 必填 |
| 2 | `host.status` | `hooks/useLibraryMetadata.ts:36`（events baseline 第一次 `initialize()`） | `{}` | `{ libraryInitialized: boolean }` |
| 3 | `library.init` | `hooks/useLibraryMetadata.ts:37` | `{}` | 只在 `libraryInitialized=false` 时发；任一 ok Envelope |
| 4 | `capabilities.get` | `hooks/useLibraryMetadata.ts:39-41` | `{}` | `{ availableOperations: string[] }`；失败被吞 → `supports()` 全 false |
| 5 | `events.read`（baseline） | `hooks/useLibraryEvents.ts:36-41、43-49` | `{ cursor: undefined, limit: 1 }` | `{ nextCursor: number, latestCursor?: number, items: [] }` |
| 6 | `candidates.list` | `hooks/useLibraryMetadata.ts:60-61` | `{ state: "pendingReview", limit: 1000, offset: 0 }` | `{ total, items: CandidateItem[] }` |
| 7 | `games.list` | `:102` | `{ played: false, limit: 1, offset: 0 }` | `{ total }`（只为侧栏计数） |
| 8 | `tags.list` | `:106` | `{}` | `{ items: TagItem[] }` |
| 9 | `roots.list` | `:110` | `{}` | `{ items: RootItem[] }` |
| 10 | `views.list` | `:114-115` | `{}` | `{ items: ViewItem[] }`，前端过滤 `kind!=="builtin" && revision!==null` |
| 11 | `notifications.list` | `:118` | `{ state: "pending" }` | `{ items: NotificationItem[] }` |
| 12 | `games.list` | `hooks/useGamesQuery.ts:92-96` | `{ search?, sort, tagId?, favorite?, played, hasCover?, viewId?, limit: 60, offset: 0 }` | `{ total, items: GameItem[] }` |
| 13 | `events.read` / `events.wait` | `useLibraryEvents.ts:58-94` | 有 `events.wait` 能力：`{ cursor, limit: 4096, timeoutMs: 25000 }`；否则 `{ cursor, limit: 4096 }` | 同 #5 |
| 14 | `plugin:window\|is_maximized` | `components/TitleBar.tsx:22-26` | `{ label: "main" }` | boolean；失败被 catch 吞 |

并发事实：#1 与 #2-#4 并行；#6-#11 在 `Promise.all` 内并发（`useLibraryMetadata.ts:93-122`）；#5 完成前 `refreshDomains` 会再 `await initialize()`（幂等，`initialization` ref 缓存 Promise，`:35-43`）。
模块加载期**没有**任何 invoke：`api.ts` 模块级只有计数器/缓存（`:18-46`），`i18n.ts:3` 只 import 打包进 bundle 的 `GameLibrary.Contracts/Localization/ui.json`。

### 1.3 让"库外壳"渲染出来的最小响应集

必须真返回：`settings.get`、`host.status`、`capabilities.get`、`events.read`、`games.list`（两处同签名）、`roots.list`、`tags.list`、`views.list`、`notifications.list`、`candidates.list`、`plugin:window|is_maximized`。

缺 `settings.get` → `SettingsProvider` 里 `settings===null`，`TitleBar` 的窗口按钮与主题按钮 disabled、`App` 内容仍能画（`SettingsDialog` 会停在"正在读取设置…"，`SettingsDialog.tsx:75-76`）。
缺 `host.status`/`events.read` → baseline 抛错被吞（`useLibraryEvents.ts:94`），`poll` 仍启动，但 `refreshDomains` 永不执行 → 侧栏计数全 0、`metaLoading` 永远 true。
缺 `games.list` → `useLibraryMetadata.ts:102` 的计数请求 reject → `refreshDomains` 整体 catch → `library.error` 显示、`metaLoading` 落 false；`GameGrid` 停在 `SkeletonGrid`（`GameGrid.tsx:74`）直到 `useGamesQuery` 自己的请求成功。

---

## 2 调用清单

`operation` 一律（`src/lib/api.ts:72`）；下表"传输"列只区分 **bridge**（`bridge_request`）与 **native**（Rust 直接命令）/ **plugin**（`@tauri-apps/*` 内部命令）。

### 2.1 游戏库根目录

| 前端函数 | 传输 | operation | 请求 | 响应 |
| --- | --- | --- | --- | --- |
| `refreshDomains(["roots"])` | bridge | `roots.list` | `{}` | `{ items: RootItem[] }`（`types.ts:143-149`） |
| `library.addRoot(path, kind?)` | bridge | `roots.add` | `{ root, kind?: "library"\|"manual" }` | 同 `RootItem`（幂等返回既有根，`lib/state.ts:226-236`） |
| `library.removeRoot(root)` | bridge | `roots.remove` | `{ rootId, expectedRevision }` | 回执（`state.ts:238-243`） |
| `App.addRoot` 选目录 | plugin | `plugin:dialog\|open` | `{ options: { directory: true, multiple: false, title } }` | `string\|null`；`App.tsx:88-93` |
| 手动添加 | plugin+bridge | 同上 + `roots.add(...,"manual")` → `games.create { sourcePath }` → `profiles.create` | `games.create` 返回 `{ gameId, launchSuggestion?: { executablePath, argv, cwd } }`（`App.tsx:95-127`） | |

`plugin-dialog` 返回值为字符串（路径）或 `null`；前端统一 `String(selected)` 或 `typeof path !== "string"` 判空（`MToolTranslation.tsx:29`、`UnityTranslationSettingsForm` 导入处 `UnityTranslationDialog.tsx:146-147`）。

### 2.2 扫描

| 前端 | operation | 请求 | 响应 |
| --- | --- | --- | --- |
| `startScan`（每个 root 一次） | `scan.start` | `{ root: string }` | `{ jobId }`（信封 `jobId` 也接受，`useScanJobs.ts:32`） |
| `cancelScan` | `scan.cancel` | `{ jobId }` | 回执；错误被吞 |
| 进度（`setInterval` 700 ms，`useScanJobs.ts:7、65-121`） | `scan.coverage` | `{ jobId }` | `{ state: string, coverage?: { scannedDirectories, candidatesFound } }`；`state ∈ succeeded/failed/cancelled` 视为终态 |

无事件推送：进度纯轮询；终态后 `refreshMeta()` + `bump()`。
候选列表与审核见 2.3；入库结果 = `candidates.accept` 返回 `gameId`。

### 2.3 候选审核（待确认）

| 前端 | operation | 请求 | 响应 |
| --- | --- | --- | --- |
| `readCandidates`（分页 ≤1000/次，循环补页） | `candidates.list` | `{ state: "pendingReview", limit, offset }` | `{ total, items: CandidateItem[] }`（`types.ts:81-90`） |
| `handleReview(items,"accept"\|"defer"\|"ignore")` | `candidates.review_batch`（有该能力时） | `{ action, items: [{ candidateId, expectedRevision, flashKind?, entryPaths?, adjustments? }] }` | `{ items: [{ candidateId, result: Envelope<CandidateReviewResult> }] }` |
| 同上回落路径 | `candidates.accept` / `.defer` / `.ignore` | `{ candidateId, expectedRevision, flashKind?, entryPaths?, adjustments? }` | `CandidateReviewResult`（`types.ts:116-122`：`reviewState, revision, gameId?, ignoreId?, similarTo?`） |
| `FlashReviewEditor` 预览 | `candidates.inspect` | `{ candidateId, flashKind?, entryPaths? }` | `FlashCandidateInspection`（`types.ts:103-109`） |
| 通知条 | `notifications.acknowledge` | `{ notificationId }` | 回执（`state.ts:286-291`） |

单条失败不中断：逐条 catch 汇总成 `"{n} 项失败：..."`（`useCandidateReview.ts:21-36`）。

### 2.4 库视图

| 前端 | operation | 请求 | 响应 |
| --- | --- | --- | --- |
| `useGamesQuery.queryPage` | `games.list` | `{ search?, sort, tagId?, favorite?, played, hasCover?, viewId?, limit: 60, offset }` | `{ total, items: GameItem[] }`，`api.ts:117-124` 逐项校验 `gameId/title/rootPath/kind/favorite/revision` |
| 排序白名单 | — | 四值：`accepted-desc`、`updated-desc`、`title-asc`、`title-desc`（`LibraryToolbar.tsx:14-19`）；`views.create` 侧另把 `title`/`recent` 归一（`:21-24`、`state.ts:258`） | |
| 分页 | — | 触底加载 `PAGE_SIZE=60`，`hasMore = games.length < total`（`useGamesQuery.ts:5、165-184`） | |
| 收藏切换 | `games.update` | `{ gameId, favorite: boolean, expectedRevision }`（批量条，`GameBatchBar.tsx:44-49`） | 回执 |
| 标签增删改 | `tags.create { name, category? }` / `tags.update { tagId, expectedRevision, name\|displayName\|color\|category\|sortOrder\|starred }` / `tags.remove { tagId, expectedRevision }` / `tags.reorder { items: [{ tagId, expectedRevision, sortOrder }] }` | `useTagActions.ts:10-16、25-31、52-57、67-70、80-84` | |
| 标签绑定 | `tags.assign` / `tags.unassign` | `{ gameId, tagId, expectedRevision }`（`DetailSheet.tsx:281-285`） | 回执 |
| 卡片状态 | — | `availability` → 文案：`available`/`suspectedMissing`/`missing`/`offline`/`accessError`/`rootUnbound`/其他=`已入库`（`GameCard.tsx:135-151`） | |
| 自定义视图 | `views.create` / `views.remove` | `{ name, search?, favoriteOnly?, hasCover?, tagId?, sort }` / `{ viewId, expectedRevision }` | 回执（`state.ts:245-283`） |

### 2.5 详情

`DetailSheet` 打开时三连并发（`DetailSheet.tsx:155-159`）：`games.get { gameId }`、`profiles.list { gameId }`、`translation.get { gameId }`。

| 前端 | operation | 请求 | 响应 |
| --- | --- | --- | --- |
| 详情 | `games.get` | `{ gameId, includeRemovalPlan?: true }` | `GameItem`（`types.ts:44-79`；`fingerprint`/`similarTo` 仅此接口带） |
| 字段保存 | `fields.set` | `{ gameId, field: "title"\|"summary", value, expectedRevision }` | 只回字段修订；前端随后重读 `games.get`（`DetailSheet.tsx:240-264`） |
| 译名保存/切换 | `titles.set_translated { gameId, title, expectedRevision }`；`titles.set_display { gameId, mode: "original"\|"translated", expectedRevision }` | `DetailSheet.tsx:243-249、506` | 回执 |
| 偏好 | `games.update` | `{ gameId, played?, rating?, expectedRevision }` | 回执 |
| 启动方式列表 | `profiles.list` | `{ gameId }` | `{ items: ProfileItem[], recommendedProfileId?: string\|null }`（`launchProfiles.ts:5`） |
| 新增 | `profiles.create` | `{ gameId, executablePath, argv: [], cwd, isDefault }` | 回执（`DetailSheet.tsx:375-385`） |
| 设为默认 / 删除 | `profiles.set_default { gameId, profileId, expectedRevision }` / `profiles.remove { profileId, expectedRevision }` | `:389-407` | |
| 自动识别 | `profiles.discover { gameId }` → 轮询 `jobs.get { jobId }` 250 ms×120（`launchProfiles.ts:19-29`） | | |
| 启动 | `launch.plan { gameId, profileId }` → `launch.execute { planId, profileId }` | `state.ts:170-215` | `launch.execute` 返回 `{ attemptId, state }`；若 `status==="accepted" && jobId` → 轮询 `jobs.get { jobId }` 300 ms 至 `succeeded/failed/cancelled`；否则 `state ∈ prepared/executing` → 轮询 `launch.status { attemptId }` 至 `processCreated/exited` |
| 移除 | `games.remove` | `{ gameId, expectedRevision }` 或 `{ gameId, expectedRevision, deleteFiles: true, confirmedPath }`（`DetailSheet.tsx:409-422`；`removalPlan` 来自 `games.get`） | 回执 |
| 打开目录 | native | `open_game_directory` | `{ path }`；Rust 侧只接受已存在的绝对目录（`lib.rs:17-31`） |
| 封面列表 | `assets.list { gameId }` | `{ items: [{ assetId, isCurrent, importedUtc? }] }`（`DetailSheet.tsx:51、215`） | |
| 封面导入 | `assets.import` | `{ gameId, sourcePath }` 或 `{ gameId, imageBase64, mimeType }`（粘贴，5 MiB 上限） | `{ assetId, warning? }` |
| 封面位图 | `assets.get` | `{ assetId }` → `{ mimeType, dataBase64 }`，前端拼 `data:<mime>;base64,<...>`（`api.ts:136-146`） | |
| 截图默认目录 | plugin | `plugin:path\|picture_dir`、`\|join`、`\|dirname` | 字符串路径（`DetailSheet.tsx:48`） |

### 2.6 名称翻译

| 前端 | operation | 请求 | 响应/行为 |
| --- | --- | --- | --- |
| 能力门 | — | `capabilities.get` 含 `titles.translate` 才显示 | `App.tsx:317、321、400` |
| 单/批量 | `titles.translate` | `{ gameIds: string[], force: boolean }` | `{ jobId }`（信封 `jobId` 也接受），见 `useTitleTranslation.ts:33` |
| 进度 | `jobs.get` | `{ jobId }`，500 ms 一次 | `{ state, result: TitleProgress\|null }`（`:37-48`） |
| 进度结构 | — | `TitleProgress { total, completed, succeeded, failed, skipped, pending, items:[{ gameId, status, reason?, provider?, patch? }], unprocessedGameIds, stopReason? }` | `items[].patch` 直接回写列表（`:6-11、43-46`） |
| 取消 | `jobs.cancel` | `{ jobId }`（`:56`） | 回执 |
| 显示偏好 | `titles.set_display` | `{ gameId, mode, expectedRevision }` | 回执 |
| 引擎偏好 | `settings.update` | `{ titleTranslationEngine: "balanced"\|"google"\|"bing", expectedRevision }` | 回 `LibrarySettings`（`SettingsDialog.tsx:141-147`） |
| 错误/限流 | — | 无专门通道：`OperationError.message` + `nextActions[].reason` | 直接落到 `role="alert"`（`DetailSheet.tsx:458-462`） |
| 忙碌门控 | — | `titleTranslation.busy` 禁用按钮 | `DetailSheet.tsx:504-505` |

### 2.7 设置

| 前端 | operation | 请求 | 响应 |
| --- | --- | --- | --- |
| 读取 | `settings.get` | `{}` | `LibrarySettings`（`types.ts:216-228`） |
| 保存 | `settings.update` | `{ ...patch, expectedRevision }` | `LibrarySettings`（`lib/settings.tsx:104`） |
| 恢复默认 | `settings.reset` | `{}` | 回执（`SettingsDialog.tsx:60`） |
| 主题 | `settings.update` | `{ theme: "dark"\|"light"\|"system" }` | 见 4.5 |
| 托盘 | `settings.update` → native `set_close_to_tray` | `{ closeToTray }` → `{ enabled }` | 回执（`lib/settings.tsx:58-60`） |
| 语言 | `settings.update` → native `set_ui_language` | `{ uiLanguage }` → `{ language }` | 回执（`settings.tsx:33`） |
| 重建缓存 | `diagnostics.cache_rebuild` | `{}` | 回执（`SettingsDialog.tsx:211`） |
| 版本 | plugin `plugin:app\|version` | `{}` | 字符串（`SettingsDialog.tsx:30`、`lib/update.ts:2`） |
| 检查更新 | **fetch**（不走 bridge） | `GET https://api.github.com/repos/sumingwang233/GameLibrary/releases/latest` | `{ tag_name, html_url, published_at }`（`lib/update.ts:5-6、35-45`） |
| 前往下载 | plugin `plugin:opener\|open_url` | `{ url }` | 无（`SettingsDialog.tsx:288`） |

### 2.8 Unity 流程

能力门：`supports("unity_translation.status")`（`App.tsx:386、401、402`）。状态机轮询 2.5 s（`UnityTranslationDialog.tsx:76-93`）。

| operation | 请求 | 响应 |
| --- | --- | --- |
| `unity_translation.status` | `{}` | `{ items: UnityTranslationItem[], needsSettings: boolean }`；条目字段 `{ gameId, title, attemptId, state, provider, reason?, profileId?, jobId?, tagged? }`（`UnityTranslationDialog.tsx:11-15`），`state` 字符串必填校验在 `:54-56` |
| `unity_translation.configure` | `{ gameIds: [string] }` | 回执（`:122`） |
| `unity_translation.restore` | `{ gameId }` | 回执 |
| `unity_translation.confirm` | `{ gameId, attemptId, success: boolean }` | 回执（`:163`） |
| `unity_translation.settings.get` | `{}` | `{ provider, endpoint, model, hasKey: boolean, boundTagId? }`（`UnityTranslationSettingsForm.tsx:6-12`），类型校验 `:70-71` |
| `unity_translation.settings.set` | `{ provider, endpoint, model, apiKey? }` | 同上；前端校验 HTTPS（本机可 HTTP）、模型名正则（`:46-58`） |
| `unity_translation.settings.import` | `{ configPath }`（plugin-dialog 选 `.ini`） | 同上 |
| 取消队列 | `jobs.cancel { jobId }`，逐一（`:173-180`） | |

`state` 取值前端硬编码：活动 `{queued, inspecting, installing}`，需处理 `{blocked, failed, needs_settings}`，待验证 `configured && tagged===true`（`:16、183-185`）。进度浮层是普通 `fixed` aside（`:188`）。

### 2.9 MTool

| operation | 请求 | 响应 |
| --- | --- | --- |
| `mtool.settings.get` | `{}` | `{ toolRoot: string\|null }`（`MToolTranslation.tsx:16`） |
| `mtool.settings.set` | `{ toolRoot }` | `{ toolRoot }` |
| `mtool.status` | `{ gameId }` | `{ state, reason, scriptPath?, backupPath? }`（`:9、48-50`） |
| `mtool.prepare` | `{ gameId }` 或 `{ gameId, profileId }` | 同上（`:81`、`lib/mtool.ts:11`） |
| 前置 | 若 `translation.get` 的 `isRequired=false` 且 `userOverride!=="NotRequired"`，先 `translation.set { gameId, override: "Required", expectedRevision }`（`:66-71`） | |

### 2.10 事件与错误

| 项 | 事实 |
| --- | --- |
| 通道 | 长轮询 `events.wait`（有该能力）或 `events.read`；无 `listen()`，无 `@tauri-apps/api/event`（全仓 `grep "@tauri-apps"` 仅 core/app/window/path/dialog/opener） |
| 载荷 | `LibraryEvent { sequence, type, entityKey?, timestampUtc?, payload? }`（`types.ts:230-237`） |
| 轮询节奏 | 有 `events.wait`：`setTimeout 0` 连发（服务端阻塞 25 s）；否则 2.5 s（`useLibraryEvents.ts:91`） |
| 事件→域 | `game.*`/`asset.*`/`profile.*`/`launch.exited`→games；`candidate.*`/`scan.*`→candidates+notifications；`tag.*`→tags+games；`root.*`→roots+games+candidates；`view.*`→views；`settings.*`→全量（`:7-20`） |
| 刷新合并 | 250 ms 合并后再 `refreshDomains`（`:83`） |
| 跨模块广播 | `window` 自定义事件：`gamelibrary-settings-changed`、`gamelibrary-unity-changed`、`gamelibrary-mtool-changed`（`:63-68`） |
| `launch.exited` | payload `{ gameId, attemptId? }` → 选中该游戏 + native `show_main_window`（`:72-81`、`api.ts:147-160`） |
| 游标失效 | `CursorExpired` / `DataEpochMismatch` / `LibraryInstanceMismatch` → 清封面缓存 + 重做 baseline（`:86-89`） |
| 错误呈现 | `library.error` 行内红字（`App.tsx:272-277`）、`actionError` 区块（`:284-292`）、详情/设置内 `role="alert"`；`ErrorBoundary` 兜渲染崩溃（`components/ErrorBoundary.tsx:14-26`） |

---

## 3 传输与运行时细节

| 主题 | 事实 |
| --- | --- |
| invoke 名 | 业务只有一个：`bridge_request`；`bridge_request` 的参数外壳是 `{ request: {...} }`，不是平铺 |
| 幂等键 | `intent` 字符串 → 稳定 key 附在 `parameters.idempotencyKey`；重试保留、成功 release（`api.ts:81-84、105-108`）。mock 需容忍未知参数 |
| 其它 native 命令 | `bridge_status`、`resolve_data_directory`（`lib.rs:285-294`，前端未见调用点）、`open_game_directory`、`set_close_to_tray`、`set_ui_language`、`show_main_window`（`lib.rs:415-423` 注册表全量） |
| plugin 调用点 | dialog：`App.tsx:5`、`DetailSheet.tsx:4`、`MToolTranslation.tsx:2`、`RootsPanel.tsx:3`、`SettingsDialog.tsx:4`、`UnityTranslationDialog.tsx:2`；path：`DetailSheet.tsx:3`（`dirname/join/pictureDir`）；opener：`SettingsDialog.tsx:5`（`openUrl`）；window：`TitleBar.tsx:3`（`getCurrentWindow().isMaximized/minimize/toggleMaximize/close/onResized`）；app：`SettingsDialog.tsx:3`、`lib/update.ts:2`（`getVersion`）。全部是 `plugin:<name>\|<command>` 形式的 invoke（Tauri v2 标准包装，推断） |
| 图片/封面 | 不走 asset 协议、不用 `convertFileSrc`：`assets.get` 拿 base64 拼 `data:` URL，LRU 缓存 + 失效（`api.ts:136-146`、`invalidateAssetChanges`）。封面的可见性由 `IntersectionObserver`（`rootMargin: "600px"`）驱动（`GameCard.tsx:29-37、39-55`） |
| 静态资源 | `/app-icon.png` 直接来自 `public/`（`TitleBar.tsx:44`、`index.html:7`） |
| 模块加载期后端调用 | 无。`settings.get` 是第一个（provider effect） |

---

## 4 DOM / CSS 事实（相机层用）

### 4.1 根节点与尺寸

| 事实 | 位置 |
| --- | --- |
| `#root` 是唯一挂载点；`html,body,#root { width:100%; height:100%; margin:0 }` | `src/index.css:109-115` |
| `body { overflow: hidden }`，页面滚动**只**发生在内部容器 | `src/index.css:117-121`；截图/缩放不要指望 `document.scrollingElement` |
| 外壳 `div.flex.h-screen.min-w-[980px].flex-col` | `components/AppShell.tsx:5` |
| 唯一滚动容器 `main.overflow-y-auto.p-6`（`ref=mainRef`，详情跳转时 `scrollTo`） | `App.tsx:241`、`:73-77` |
| 侧栏 `aside.w-[var(--sidebar-width)]`，`--sidebar-width: 232px` | `Sidebar.tsx:66`、`index.css:52` |
| 标题栏 `header.h-12`，`data-tauri-drag-region` 两段 | `TitleBar.tsx:42-43、55` |
| 网格 `grid-cols-[repeat(auto-fill,minmax(var(--grid-min-card),1fr))]`，`--grid-min-card: 180px` | `GameGrid.tsx:13`、`index.css:54` |
| 窗口 `1320×820`、`min 980×640`、`decorations:false`、`dragDropEnabled:false` | `src-tauri/tauri.conf.json:13-24` |

### 4.2 `position: fixed` 清单

| 元素 | 类 | z | Portal |
| --- | --- | --- | --- |
| Sheet 遮罩 | `fixed inset-0 bg-black/60` | 40 | 是（Radix `Dialog.Portal` → body） |
| Sheet 面板 | `fixed inset-y-0 right-0 w-[min(var(--sheet-width),92vw)]`（`--sheet-width:460px`） | 50 | 是 |
| ConfirmDialog 遮罩 / 面板 | `fixed inset-0` / `fixed top-1/2 left-1/2 -translate-x/y-1/2` | 50 / **60** | 是 |
| PromptDialog 遮罩 / 面板 | 同上 | 50 / **60** | 是 |
| Select 弹出层 | `relative z-50 max-h-(--radix-select-content-available-height)` | 50 | 是 |
| Tooltip | `z-50`（含 Arrow） | 50 | 是 |
| Unity 进度浮层 `aside` | `fixed right-4 bottom-4 max-h-[40vh] w-[min(400px,92vw)]` | 30 | **否**（直接挂在 App 内） |
| 批量条标签选择器背景 | `fixed inset-0`（点空白关闭） | 10 | 否；列表本体 `absolute z-20` |
| TagsPanel 同类背景 | `fixed inset-0` | 10 | 否 |

来源：`ui/sheet.tsx:33、38、61`、`ui/confirm-dialog.tsx:38、43`、`ui/prompt-dialog.tsx:47-48`、`ui/select.tsx:64`、`ui/tooltip.tsx:44、50`、`UnityTranslationDialog.tsx:188`、`GameBatchBar.tsx:80-81`、`TagsPanel.tsx:415-418`。

### 4.3 视口依赖

| 依赖 | 位置 | 影响 |
| --- | --- | --- |
| `h-screen`（=100vh） | `AppShell.tsx:5` | 在缩放容器里 `100vh` 仍取窗口高，元素会"超出"缩放后的框 |
| `min-h-screen` | `ErrorBoundary.tsx:17` | 仅崩溃兜底页 |
| `max-h-[40vh]` | `UnityTranslationDialog.tsx:188` | 同上 |
| `IntersectionObserver(rootMargin 600px)` | `GameCard.tsx:29-34` | 相对**视口**判定；已 `disconnect()`，只触发一次 → 相机层若把内容缩到视口外，封面永不加载 |
| `content-visibility:auto` + `contain-intrinsic-size:auto 275px` | `GameCard.tsx:74` | 卡片自带 containment/尺寸占位；测量 DOM 盒需考虑该占位 |
| `ResizeObserver` | 全仓无 | — |
| `window.innerWidth/innerHeight` | 全仓无 | — |
| `matchMedia("(prefers-color-scheme: dark)")` | `lib/settings.tsx:27` | 仅 theme=`system` 时读一次 |

### 4.4 会"逃出"缩放容器的因素

- 所有 `fixed`（4.2）：若相机层用 `transform` 建立 containing block，fixed 会相对该祖先定位——**这反而被约束**；若相机层只做 `zoom`/改 `width`，fixed 仍贴窗口 → 逃逸。
- `transform` 使用点：卡片 `hover:-translate-y-1`、封面 `group-hover:scale-[1.02]`（`GameCard.tsx:75、90`）、对话框 `-translate-x/y-1/2`（4.2）。
- `filter`/`backdrop-filter`/`will-change`/`contain`：全仓未直接使用；`contain` 只经 `content-visibility` 隐式出现（卡片）。
- 滚动条样式为 `::-webkit-scrollbar 10px`（`index.css:135-138`），WebKit 系固定占位，缩放后宽度不随之变化。

### 4.5 主题与语言

| 事实 | 位置 |
| --- | --- |
| 暗色是默认，令牌在 `@theme` → `:root`；浅色靠 `html.light` 覆盖同名变量 | `index.css:10-41`、`:57-89` |
| 切换点唯一：`document.documentElement.classList.toggle("light", …)` | `lib/settings.tsx:32-35` |
| 手动切换入口：标题栏按钮，读当前 class 反推目标后写 `settings.update` | `TitleBar.tsx:32-38` |
| 语言：`document.documentElement.lang` + 订阅式 `t()`，文案来自 bundle 内 `GameLibrary.Contracts/Localization/ui.json` | `lib/i18n.ts:16-31` |
| 字体：`--gl-font-family` 写在 `documentElement.style` | `settings.tsx:38-40`、`index.css:92` |

---

## 5 可 Mock 性结论

### 5.1 最小 operation 集合（按重要性）

| P | operation | 不实现会怎样 |
| --- | --- | --- |
| 0 | `settings.get` | 主题/语言/字体不应用，标题栏按钮禁用 |
| 0 | `host.status` | baseline 抛错，元数据永不加载 |
| 0 | `capabilities.get` | 降级路径全开（逐条 review、`events.read` 轮询），可用但慢；**建议显式只声明想要的能力** |
| 0 | `events.read` | 事件循环每 2.5 s 报错一次（错误被捕获，不白屏） |
| 0 | `games.list` | 网格骨架屏常驻 / `library.error` |
| 1 | `roots.list`、`tags.list`、`views.list`、`notifications.list`、`candidates.list` | 侧栏计数为 0、列表空 |
| 2 | `games.get`、`profiles.list`、`translation.get`、`assets.list`、`assets.get` | 详情面板打不开（首屏 Promise.all 任一 reject → 错误条） |
| 3 | `settings.update`、`settings.reset`、`show_main_window` | 设置页交互报错 |
| 4 | `scan.start`/`scan.coverage`/`scan.cancel` | 扫描按钮报错；注意 `scan.coverage` 必须给出终态，否则 700 ms 轮询永不停止 |
| 5 | `launch.plan`/`launch.execute`/`jobs.get`（或 `launch.status`） | 点"开始游戏"报错 |
| 6 | `unity_translation.*`、`mtool.*` | 只要 0 级能力未声明这些，整块 UI 不出现 |

### 5.2 必须"真有返回值"的调用

`settings.get`（否则 `settings===null`，设置/主题/语言全废）、`host.status`（`data.libraryInitialized` 为 `false` 会追加 `library.init`）、`capabilities.get`（`data.availableOperations` 必须是数组，否则 catch 成空集）、`events.read`（`nextCursor`/`latestCursor` 至少一个 number）、`games.list`（`data.items` 必须是数组，否则 `api.ts:117-124` 直接 throw）、`games.get`（六字段校验）、`roots/tags/views/notifications/candidates.list`（`data.items` 数组）。

### 5.3 响应骨架

```jsonc
// settings.get
{ "requestId": "mock-1", "ok": true, "status": "ok", "libraryInstanceId": "mock-lib",
  "dataEpoch": "1", "data": { "revision": 1, "theme": "dark", "uiLanguage": "zh-CN",
  "uiFontFamily": "Microsoft YaHei UI", "closeToTray": false, "autostartEnabled": false,
  "scanIntervalMinutes": 30, "titleTranslationEngine": "balanced",
  "cacheParentDirectory": null, "activeViewId": null } }

// host.status
{ "ok": true, "status": "ok", "requestId": "mock-2", "data": { "libraryInitialized": true } }

// capabilities.get （只声明想启用的能力；留空数组 = 全部走降级路径）
{ "ok": true, "status": "ok", "requestId": "mock-3",
  "data": { "availableOperations": ["titles.translate", "candidates.review_batch",
  "tags.update", "profiles.discover", "mtool.prepare", "unity_translation.status"] } }

// events.read / events.wait
{ "ok": true, "status": "ok", "requestId": "mock-4",
  "data": { "nextCursor": 0, "latestCursor": 0, "items": [] } }

// games.list（page）
{ "ok": true, "status": "ok", "requestId": "mock-5",
  "data": { "total": 1, "items": [ { "gameId": "g1", "title": "示例游戏",
  "originalTitle": "Sample", "translatedTitle": null, "titleDisplayMode": "original",
  "summary": "", "rootPath": "D:\\Games\\Sample", "kind": "folder", "engine": "unity",
  "favorite": false, "played": false, "rating": 0, "revision": 1,
  "coverAssetId": null, "availability": "available", "playtimeMinutes": 0,
  "lastPlayedUtc": null, "acceptedUtc": "2026-01-01T00:00:00Z",
  "updatedUtc": "2026-01-01T00:00:00Z", "tags": [] } ] } }

// games.get —— 额外带 fingerprint / similarTo / removalPlan（可 null / []）
{ "ok": true, "status": "ok", "requestId": "mock-6",
  "data": { /* 同上全部字段 */ "fingerprint": null, "similarTo": [],
            "removalPlan": { "targetPath": "D:\\Games\\Sample", "scope": "directory", "error": null } } }

// profiles.list
{ "ok": true, "status": "ok", "requestId": "mock-7",
  "data": { "items": [ { "profileId": "p1", "gameId": "g1",
  "executablePath": "D:\\Games\\Sample\\game.exe", "argv": [], "cwd": "D:\\Games\\Sample",
  "isDefault": true, "revision": 1, "source": "manual", "validationStatus": "verified" } ],
  "recommendedProfileId": "p1" } }

// translation.get
{ "ok": true, "status": "ok", "requestId": "mock-8",
  "data": { "userOverride": "Auto", "inherited": "Auto", "effective": "NotRequired",
            "isRequired": false, "revision": 1 } }

// launch.execute（accepted 形态：前端转去轮询 jobs.get）
{ "ok": true, "status": "accepted", "requestId": "mock-9", "jobId": "job-1",
  "data": { "attemptId": "a1", "state": "queued" } }

// jobs.get
{ "ok": true, "status": "ok", "requestId": "mock-10",
  "data": { "state": "succeeded", "error": null, "result": { "ok": true, "status": "ok", "requestId": "inner", "data": {} } } }

// scan.coverage（必须最终进入终态，否则 700ms 轮询不停）
{ "ok": true, "status": "ok", "requestId": "mock-11",
  "data": { "state": "succeeded", "coverage": { "scannedDirectories": 12, "candidatesFound": 3 } } }

// 失败信封（OperationError 走这里；nextActions 会拼进用户文案）
{ "ok": false, "status": "failed", "requestId": "mock-12",
  "error": { "code": "RevisionConflict", "message": "对象已被修改", "retryable": true, "recoveryOperation": null },
  "nextActions": [ { "operationId": "games.get", "actionId": null, "reason": "重新读取后再试" } ],
  "data": {} }
```

### 5.4 浏览器侧注入点

| 需要 mock 的 | 形态 |
| --- | --- |
| `bridge_request` | `window.__TAURI__.core.invoke` 覆盖即可（`api.ts:83` 优先读它）——不必实现 `@tauri-apps/api` 本身 |
| 其余 native | 同一条 `invoke` 里按 `cmd` 分支：`open_game_directory`、`show_main_window`、`set_close_to_tray`、`set_ui_language`、`bridge_status` |
| plugin 命令 | `plugin:dialog\|open`（返回路径或 `null`）、`plugin:path\|picture_dir\|join\|dirname`、`plugin:app\|version`、`plugin:opener\|open_url`、`plugin:window\|is_maximized` 等（`Label` 参数 `{ label: "main" }`） |
| 定时行为 | `events.read` 2.5 s 轮询、`scan.coverage` 700 ms、`jobs.get` 300 ms、unity 2.5 s —— mock 需保证这些调用可重入且幂等 |

> 若 `capabilities.get` 里声明 `events.wait`，前端会以 `timeoutMs: 25000` 阻塞长轮询并 `setTimeout 0` 连发；浏览器 mock 建议**先不声明**，走 2.5 s `events.read`。
