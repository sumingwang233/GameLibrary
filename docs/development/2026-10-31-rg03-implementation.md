# RG-03 实现记录与统一验收清单

2026-10-31。基于 RG-02 的 v1.8.2 草稿继续实现，遵从用户“等所有重构优化完成之后统一验收”的安排。本轮没有运行测试、类型检查、构建、桌面或性能采样；结构图谱同步属于源码静态索引，不代表运行验收。

分支仍为 `codex/optimize-footprint-v1.8.1`，独立 worktree 为 `C:\Users\sumingwang\.codex\worktrees\optimize-footprint-v181\GameLibrary`。main、正式实例、真实游戏库与远端实现均未操作，没有安装或升级依赖。

## 已写入的实现

| 范围 | 代码行为 | 本地提交 |
| --- | --- | --- |
| F1：列表与封面 | 网格/紧凑布局只挂载视口附近行，额外保留焦点所在行；按真实卡片尺寸与响应式列数计算位置。已加载的游戏数据、批量勾选、触底分页与滚动恢复保留。屏外卡片释放自身封面状态，继续使用既有 64 MiB 数据 URL 缓存。 | `46c1812` |
| F2：后台刷新 | Rust 在隐藏、显示及窗口状态变化时发送内部可见性事件。隐藏期间继续消费事件、处理库身份失效与游戏退出唤醒，但合并普通刷新域和插件通知；恢复可见后刷新。Unity 状态空闲兜底从 2.5 秒降为 30 秒，有活动任务或错误时仍为 2.5 秒，隐藏时不启动新轮询。 | `46c1812` |
| F3：按需加载 | 详情、设置、待审核、目录、标签面板及 Unity 设置表单采用 React lazy/Suspense。详情与设置关闭后卸载；SettingsProvider 和 Unity 状态控制器仍常驻，避免丢失外观设置或自动提示。加载提示补齐四语言。 | `46c1812` |
| F4：AOT 准备 | HostClient 握手/请求/结果及 Bridge 私有帧使用 SDK 内置的 JSON 源生成元数据。增加显式 NativeAOT 发布配置，保留默认压缩自包含发布方式。 | `d3d7387` |
| 重连结果边界 | 引导期写请求重连时若出现新的库身份，仍拒绝重绑和重发；错误结果的 data 显式为 JSON null，避免未定义 JsonElement 使错误结果无法序列化。新增对应假管道回归用例。 | `d3d7387` |

审查期间也处理了 Unity 状态请求在途时收到新事件的情况：保留待刷新标记，在当前请求结束后立即补读，避免错过事件后等满 30 秒。每轮 effect 有自己的退出标记，已关闭的轮询不能因异步请求完成而重新挂上计时器。

本轮未改动 Application、Domain、Host 和 Infrastructure 的源码，保留已合入的 v1.8.1 扫描边界及去重修复。不涉及数据库迁移。IPC 长度帧、业务字段、状态字符串、空值约定、权限、库身份/纪元和 requestId 保持原设计；新增的 JSON 元数据路径仍需交叉序列化及 IPC 回归确认。

## 边界与限制

- 列表使用共享的实测行高，适用于当前等高卡片/紧凑行。以后引入可变高度正文时应改成逐行测量。已在源码标注该限制。
- 保留轻量游戏对象以维持批量选择和分页语义；没有把“DOM 有界”描述为所有前端内存都有硬上限。缓存预算也不包含浏览器解码图像、传输中的请求或整个 WebView。
- 仅隐藏或最小化时延后普通刷新，不把失去焦点当作隐藏；不暂停 Host 的扫描、游玩跟踪或写入操作。隐藏前已经发出的请求可以完成。
- NativeAOT 当前是待试编的候选。本机查看到 ILCompiler 10.0.12 缓存，不代表工具链、全部还原元数据或运行兼容性已经验证。未把 PublishAot 加入默认 sidecar 脚本，尚无 AOT 体积或内存结果。
- RG-02 的 29.7% Private Bytes 样本仅是上一代码检查点的 Bridge/Host 辅助数据，不作为本轮前端与序列化改动的收益。

内部事件为 `gamelibrary-window-visibility`，布尔载荷仅表示主窗口的可见/非最小化状态；不是 Host 公共 IPC。前端注册监听后再读取初始窗口状态，并隔离迟到快照和已注销监听。浏览器开发模式使用 document visibility。

实现采用 [React lazy](https://react.dev/reference/react/lazy) 和 [.NET JSON 源生成](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) 的现有机制，没有添加虚拟列表或序列化依赖。NativeAOT 的适用范围及限制见 [微软文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。

## 已准备、尚未执行的回归

| 检查源码 | 覆盖意图 |
| --- | --- |
| useVirtualRows.test.ts | 大库窗口范围、边界收缩、空库、焦点行单独保留 |
| GameCard.test.tsx | 屏外封面引用释放、缓存复用、长列表卸载与焦点/勾选保留 |
| windowVisibility.test.ts | 原生状态快照迟到、监听注销竞态、重复事件、浏览器模式 |
| hooks.test.ts | 隐藏期间事件持续消费、刷新域合并、退出唤醒与显示后补齐 |
| UnityTranslationDialog.test.tsx | 隐藏暂停/显示恢复、空闲低频轮询、请求在途时的新事件 |
| IpcJsonSerializationTests.cs | 六种状态字符串、null 字段、中文载荷、握手权限、身份及帧往返、拒绝整数枚举 |
| HostConnectionRecoveryTests.cs | 重连/半帧/错配请求，以及引导期写入被新库身份拒绝后结果可序列化 |

没有将新增用例计为通过，也没有沿用旧构建产物作为当前源码证据。

## 统一验收次序

以下命令仅为后续验收入口，本轮没有执行。必须在上述 worktree 运行，把 TEMP/TMP、合成游戏库、日志、解压缓存和 WebView 数据全部限定在其 `.tmp/rg03/`，并使用独立测试 identifier。只使用已有依赖；缺少缓存或还原元数据时停止，不自动安装、升级或放宽权限。

1. 前端定向用例与类型检查。工作目录为 `src/GameLibrary.Tauri`：

   ```powershell
   npm.cmd test -- src/lib/api.test.ts src/lib/assetCache.test.ts src/lib/hooks/hooks.test.ts src/lib/hooks/useVirtualRows.test.ts src/lib/windowVisibility.test.ts src/components/GameCard.test.tsx src/components/UnityTranslationDialog.test.tsx src/components/UnityTranslationSettingsForm.test.tsx src/components/DetailSheet.test.tsx src/components/DetailSheet.gameProp.test.tsx src/components/Sidebar.test.tsx src/components/TitleBar.test.tsx src/lib/i18n.test.tsx
   npm.cmd run typecheck
   ```

2. Rust Release 单元检查。工作目录为 `src/GameLibrary.Tauri/src-tauri`：

   ```powershell
   cargo test --release --lib --offline
   ```

3. .NET 线协议、权限及恢复检查。工作目录为仓库根；设置 worktree 内的结果目录：

   ```powershell
   dotnet test tests/GameLibrary.ContractTests/GameLibrary.ContractTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~IpcFrameTests|FullyQualifiedName~ClientPermissionProfileTests' --results-directory .tmp/rg03/test-results
   dotnet test tests/GameLibrary.IntegrationTests/GameLibrary.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~IpcJsonSerializationTests|FullyQualifiedName~HostConnectionRecoveryTests|FullyQualifiedName~PermissionDeclarationTests|FullyQualifiedName~EventStreamTests|FullyQualifiedName~ScanBoundaryRegressionTests|FullyQualifiedName~DirectoryWalkerTests|FullyQualifiedName~ScanOperationTests' --results-directory .tmp/rg03/test-results
   dotnet test tests/GameLibrary.HeadlessE2ETests/GameLibrary.HeadlessE2ETests.csproj -c Release --no-restore --filter 'FullyQualifiedName~TauriBridgeE2ETests|FullyQualifiedName~HostStopE2ETests' --results-directory .tmp/rg03/test-results
   ```

4. 默认压缩 sidecar 与桌面 Release 编译。沿用临时配置覆盖办法，测试 identifier 与正式实例分离；使用 `--no-bundle`，不调用总打包入口。先定位此前 Tauri setup 与 VSTest 的拒绝访问，再执行桌面功能与过程检查，不能将启动失败归为已排除的环境问题。

5. NativeAOT 显式试编。仅对 Bridge，在仓库根执行下列候选入口；默认 Host 与正式发布脚本不变：

   ```powershell
   dotnet publish src/GameLibrary.TauriBridge/GameLibrary.TauriBridge.csproj -c Release --no-restore -p:PublishProfile=NativeAot -o .tmp/rg03/native-aot
   ```

   该命令也可能因还原元数据缺失而停止，不能自行改成在线安装。成功后必须用这一个实际 AOT 可执行文件重跑并发长轮询、中文路径、乱序关联、断连恢复、业务/传输错误和退出清理，不能拿默认测试目录中的普通 Bridge 充当 AOT 证据。比较通过后再决定是否切换默认发布。

6. 桌面统一人工与短时对比：网格/紧凑两布局，大库连续滚动、窗口缩放、键盘 Tab/Shift+Tab、焦点行保留、批量选择、触底分页、详情修改后滚动恢复、封面更换/迟到响应；隐藏时制造事件并恢复，检查游戏退出唤醒；首次打开各延迟模块，检查主题、语言、关闭到托盘和 Unity 自动提示。

7. 相同合成库和 Release 设置下，分别测试 v1.8.1、默认候选及可用的 AOT 候选。先结束编译等干扰，再预热、重复采样整棵桌面进程树的 Private Bytes、Working Set、CPU 与启动耗时；另外记录安装后文件字节数。不得由缓存预算或侧车样本推算整桌面收益。

## 本轮交付状态

F1–F4 的实现与候选配置已保存，本轮只是待统一验收的代码检查点。F5 完成静态审查、文档和结构索引同步；该索引不包含构建后生成的实际 JSON 源码。NativeAOT 默认链路切换、性能收益和全部功能验收均未宣称完成。后续仍不自动推送、合并 main、生成安装器或发布。

后续新增作业结果转存、封面队列和保守增量刷新，见 [RG-04 实施与统一验收增量清单](2026-10-31-rg04-implementation.md)。统一验收须合并该清单，不能只运行本记录的早期用例列表。
