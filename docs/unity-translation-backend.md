# Unity 翻译自动化接口

配置操作只下载固定公开插件并做离线验证，不启动游戏或调用翻译 API。

## v1.7.4 已有插件与标签补处理

2026-10-04：启动时重新检查带绑定未翻译标签的 Unity 记录，标签新增和显式配置复用同一流程。已有 Rei/BepInEx 加载器通过检查、目标为 `zh/zh-cn/zh-hans`、翻译未被关闭，并能从受支持端点程序集的常量 `get_Id` 元数据确认所选端点时，记录 `confirmed` 并解除绑定的用户标签。DeepSeek 配置还须有本机非空 key。此检查不加载插件代码、不发送网络翻译请求、不改变现有游戏文件；仅存在 DLL 不足以清理标签。动态端点 ID、配置不完整及用户已 `declined` 的记录保留标签，允许用户从详情主动重试。新安装仍保留原有两次确认。

用例覆盖启动/标签/显式配置三种已有插件入口，以及非中文、关闭、未知端点、缺 key、用户否定、端点类型无效六种保留标签情形。发布准备的完整验证见 [v1.7.4记录](releases/v1.7.4-validation.md)。

## v1.7.4 大小写兼容修复（未发布）

Release 全方案构建0警告/0错误，变更C#文件的 `dotnet format --verify-no-changes`、四语言检查、版本/README门禁及 `git diff --check` 通过。code-review-graph 修改前查询上下文，修改后增量刷新解析错误0，快照与运行流已同步。

扫描与历史游戏记录使用 `unity`，原自动化服务的六处筛选及详情页按钮却只接受 `Unity`。结果是已添加「未翻译」标签、已有服务商设置的游戏仍没有任何配置尝试，启动时找不到内置插件并继续查找 MTool。测试原先使用大写引擎记录，遗漏了这个真实输入。

追溯证据：`ContractJson.cs:17` 从初始提交 `5c9209d` 就使用 `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`；`ScanCandidate.ToDetail` 输出 `EngineId` 枚举，`ScanCandidatePersistence.cs:60` 经 `ContractJson.Options` 序列化，因此 `EngineId.Unity` 的规范值是 `unity`。v1.7.2 提交 `47690cd` 同时引入服务的六处 `"Unity"` 判断、详情页的大写判断和测试 fixture 的 `Engine = "Unity"`。fixture 直接 `InsertGame`，未走扫描/序列化流程，这是错误实现与错误测试输入互相掩盖的原因；小写数据本身符合契约。修复提交为 `788ba40`。

2026-10-02 按用户要求登记开发教训：跨层字符串判断先追溯生产者、公共序列化、存储/API 和消费者，区分枚举名/规范值/显示文案；搜索并修正所有同类调用及前端条件；测试从实际生产链路获取输入或使用确认过的规范值，不照抄实现的假设；先复现再修复。不为迎合错误判断改变既有数据或全局序列化。该要求已写入项目 `AGENTS.md` 第9条及本机 Codex 持久记忆；记忆不记录个人游戏目录或密钥。

后端复用 `IsActiveUnityGame`，以 `OrdinalIgnoreCase` 比较引擎，覆盖启动补处理、标签触发、服务设置后的批处理、已有配置发现、安装初检与写入前复核。详情页采用同样的大小写兼容判断。测试默认引擎改为扫描使用的小写值，另覆盖 `unity`、`Unity`、`UNITY` 的启动、标签触发和详情重试。没有修改数据库 schema、启动 Profile、配置回滚或凭据存储方式。

2026-10-02 真实验证：修复前专项4项中3项失败（小写/全大写的启动补处理，以及小写游戏的已有插件配置）；前端13项中小写/全大写按钮用例2项失败。修复后 `UnityTranslationTests|TranslationConfigTests` 52项全部通过，详情页与 Unity 弹窗/设置专项17项全部通过；前端 typecheck、生产构建通过，构建仍有已有的单块超过500 kB提示。测试仅使用临时合成游戏和随机测试密钥。实际运行库与游戏目录仅作只读元数据/文件存在检查；没有读取私人密钥或游戏中的密钥配置，没有运行实际游戏。当前安装的 v1.7.3 未替换，v1.7.4 未打包、未推送、未发布；实际游戏内翻译效果待更新后由用户确认。

| 操作 | 参数 | data |
| --- | --- | --- |
| `unity_translation.settings.get` | `{}` | settings 元数据 |
| `unity_translation.settings.set` | `{provider?,endpoint?,model?,apiKey?,boundTagId?,enabled?}` | settings 元数据 |
| `unity_translation.settings.import` | `{configPath?}` | 成功返回 settings；无法唯一选择时 `{imported:false,sources}` |
| `unity_translation.configure` | `{gameIds:string[]}`，1–1000 项 | Accepted 信封含 `jobId`，data=`{jobId,gameIds}` |
| `unity_translation.status` | `{gameId?}` | `{items,needsSettings}` |
| `unity_translation.pending` | `{}` | `{items,needsSettings}`，仅 `needs_settings/configured` |
| `unity_translation.confirm` | `{gameId,attemptId,success:boolean}` | 当前 item |
| `unity_translation.restore` | `{gameId}` | 当前 item，队列正在配置时拒绝恢复 |

settings：`{provider:"deepseek"|"openai",endpoint,model,hasKey,boundTagId,enabled,configured,sourceLanguage:"auto",targetLanguage:"zh",sources}`。

source：`{configPath,gameId,title,provider,endpoint,model,hasKey:true}`。不返回密钥或密钥摘要。启动时发现的唯一有效配置直接导入；多个不同配置仅返回来源供选择。没有密钥时 `settings.get` 返回来源；`settings.import({})` 可再次自动发现。

item：`{gameId,title,attemptId,state,provider,reason?,jobId?,profileId?}`。状态为 `needs_settings|queued|inspecting|installing|configured|confirmed|declined|blocked|failed|restored`。

事件：`unity_translation.configured` 的 payload 为 configured item；其他状态变化发 `unity_translation.updated`；服务商变更发 `unity_translation.settings_changed`。不得只依赖事件 baseline；初始化与重连应读 `pending`，详情读 `status`。这些操作不会要求唤醒或显示主窗口。

统一 `launch.execute` 对目标游戏的 `queued/inspecting/installing` 状态返回可重试 `MaintenanceMode`，显式 profile 与 plan 入口均生效；其他游戏不受该检查影响。restore 写文件期间暂用 `installing` 状态，完成/失败后退出；启动批次在 IPC 开始接收请求前登记，关闭启动时的状态检查窗口。

`confirm(false)` 在运行游戏前后均可使用，保留配置和标签，写入 `declined`，该 attempt 不再出现在 pending。`confirm(true)` 仅接受最新 configured attempt，且要求同游戏、同 profile、同 EXE 在配置完成后有实际 `processCreated/exited` 与进程启动时间记录；只解除当时绑定且仍有效的用户标签。旧 attempt、旧 dataEpoch、绑定变化均拒绝。

服务商地址只允许 HTTPS（loopback 允许 HTTP），拒绝 userinfo/query/fragment。省略 apiKey 保留本机密钥，但切换 provider/origin 必须提供新密钥；metadata 包含新/旧密钥均拒绝。默认模型为 `deepseek-flash`；已有配置的模型保留。自定义端点关闭 DeepSeek 专用 thinking/assistant flags 和 Debug。

接线：`HostRuntimeState.UnityTranslations.RequestForTaggedGame(gameId)`，标签处理器只在实际新增关联后调用。现有 `JobManager` kind=`unityTranslation`；实时进度 `{completed,total,gameId,state}`，最终进度 `{completed,total,items}`。`jobs.get` 应暴露该 kind 的 result。Contracts/catalog/schema 由父代理登记；五个写操作加入 `ReceiptOperations`，要求 `idempotencyKey`，RequestDigest 仅保存 SHA-256，不把原始请求参数写入收据。无新增数据库 DDL。

`app_settings` 仅保存 `unity_translation.settings` 与 `unity_translation.game.<id>` 的无秘元数据。设置中的 CredentialId 引用独立 CurrentUser DPAPI 密钥文件，换密钥不覆盖旧文件，数据库写失败或恢复旧设置时仍使用原密钥引用；vault scope 以规范数据目录为准，库实例/epoch 更换不丢旧引用。首次发现未翻译标签后持久绑定 TagId，改名不失效。备份原始 INI、插件和 bootstrap 使用 CurrentUser DPAPI 加密，放在工作区外 `%LOCALAPPDATA%/GameLibrary/UnityTranslation/vault/<scope>/backups`。恢复先检查所有 hash，发现其他程序修改则保留文件并拒绝恢复。只读/运行中/未知或冲突加载器/IL2CPP 保留标签并写原因。未完成安装在下次启动尝试回滚；若游戏运行中或文件已变化，则阻断并提示人工检查。

固定资源：

- [XUnity.AutoTranslator ReiPatcher v5.0.0](https://github.com/bbepis/XUnity.AutoTranslator/releases/tag/v5.0.0)，ZIP SHA-256 `71DB63000A5487E03D2DBE692C20EB970167DB469394B5DBA2BE5F6343B9D6E8`。
- [DeepSeekTranslate v0.1.14](https://github.com/Tabing010102/DeepSeekTranslate/releases/tag/v0.1.14)，DLL SHA-256 `8FDC8C26E0C3129350541CE84EA0C818D980626E848991A9D1C4AC8FFC4C4CA7`。
- [DeepSeek 官方模型说明](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)：新配置默认 `deepseek-flash`。

WindowsPowerShell 隐藏助手只调用 `.PrePatch/.Patch(PatcherArguments)` 和反射加载验证。不会调用官方 setup 的入口（它含 `Console.ReadKey()`）、PatchAndRun、插件 Initialize/Translate 或游戏。XUnity 根目录依据其 `parent(Application.dataPath)`，支持 EXE 位于 `*_Data` 内；不改变用户手动启动 profile。

验证：`dotnet test tests/GameLibrary.IntegrationTests/GameLibrary.IntegrationTests.csproj --filter "FullyQualifiedName~UnityTranslationTests|FullyQualifiedName~TranslationConfigTests"`。测试仅使用合成程序集和临时随机密钥，不读个人配置。实际固定 patcher 对合成 Unity assembly 的离线检查已确认两个 Bootstrap 调用，固定端点与 v5.0.0 Core 的反射加载 exit=0；真实游戏显示和付费翻译结果需用户主动启动验收。
