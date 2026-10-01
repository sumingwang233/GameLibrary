# Unity 翻译自动化后端接口（v1.7.2，未发布）

仅后端。配置操作只下载固定公开插件并做离线验证，不启动游戏或调用翻译 API。

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
