# RG-02-r2：基于 v1.8.1 的单 Bridge 优化记录

2026-10-30。状态：实现已整合，完整验收未通过；未推送实现、打包或发布。

## 源码与范围

用户要求以修复严重扫描问题后的最新版继续。核对时 GitHub main 仍为 `be8e143`，已发布 v1.8.1 的源码位于 `codex/release-v1.8.1`，提交 `4f7da8a072cf815d5c4ab1f647f1a4467bccc586`。保留重构快照 `8b006c6` 后，以合并提交 `d4c6700` 纳入该发布分支。

工作分支仍为 `codex/optimize-footprint-v1.8.1`，版本字段改为 v1.8.2（未发布），避免与已有正式版本混淆。所有修改、构建和合成库均位于本独立 worktree；正式实例及其游戏库未操作。

相对上述发布源码，以下目录的源码差异检查为零：Application、Domain、Host、Infrastructure、Tauri 的前端 src、IntegrationTests、UnitTests。因此扫描根边界、ExactPath 子目录遍历、快捷方式去重、Unreal 身份保守判定、Folder.lnk 目录及 retirement 范围修复全部保留。源码一致性不替代测试执行。

## 改动

- 一个 C# Bridge，普通请求和 `events.wait` 各持有一条 HostConnection，两条通道分别串行、彼此并行。Rust 侧最多两个请求在途。
- 私有 stdio 帧采用独立 `transportId` 关联乱序响应。Host 的业务 `requestId`、公共 IPC、权限配置、库身份/纪元校验和 SQLite schema 不变。
- 超时、EOF、协议损坏失败当前进程的所有等待请求；每个进程拥有自己的待响应表，旧会话不能清理替代会话。Host 已返回的业务 Envelope 与传输失败分开，前端原有幂等键保留行为不变。
- 退出时主动关闭 Bridge stdin；C# 取消阻塞读取与长轮询，仅向成功连接过的数据目录发送 `host.stop`。无效目录请求不覆盖退出范围。
- 本机复现 `Process.WaitForExitAsync` 拒绝访问，导致原监视任务提前故障、退出清理被跳过。现仅对 Win32 错误 5 使用同步低权限等待，250ms 检查取消；监视异常也保证执行已连接 Host 的退出清理。未改变系统权限或提权。
- 桌面脚本增加整棵进程树采样选项，Bridge 数量断言改为一个；测试输出路径不再指向 main。

低权限等待依据 .NET 10 的 [Process.Windows.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs)：异步事件监视经 `GetOrOpenProcessHandle` 打开进程，同步 `WaitForExitCore` 仅请求 `SYNCHRONIZE`。本机最小诊断中异步等待抛出错误 5，而有限查询可正常读取存活状态。该修正不修改 VSTest 自身，因此不解决测试宿主的同类故障。

## 发布体积

R1 的单文件压缩与去除重复 publish 已纳入正式 v1.8.1，现在属于基线。普通用户载荷继续采用 Tauri 生成的 Host/Bridge，Tools 仍使用独立 Console Host，自包含方式、文件名和布局不变。

| 字节数 | v1.8.1 已发布基线 | v1.8.2 当前候选 |
| --- | ---: | ---: |
| Host | 40,671,974 | 40,671,953 |
| Bridge | 37,650,530 | 37,653,970 |
| 两个 sidecar 合计 | 78,322,504 | 78,325,923 |

本轮相对 v1.8.1 增加 3,419 字节，未取得新的磁盘节省，不将“sidecar 合计下降”记为通过。此前相对 v1.8.0 的约 49.6% sidecar 缩减已属于 v1.8.1。没有生成安装器，也没有测量下载包大小，不能由单文件压缩推算下载收益。

基线来自既有 `GameLibrary-Portable-win-x64-v1.8.1.zip`，SHA-256 为 `d664cb57a1e76b510f6842146cd07545a511c5f8adfd5879be06fecc35dc4d70`，与发布清单一致。解压前核对 ZIP 项目仅为三个可执行文件与清单，解压到当前 worktree 的 `.tmp/rg02/v182/baseline-v181`。未生成或修改发布包。

## 验证状态

| 项目 | 结果与范围 |
| --- | --- |
| 关键修复源码一致性、git diff --check | 通过 |
| 前端 API、事件 hooks、Sidebar 定向测试 | 3 个文件，30 项通过 |
| 前端 TypeScript 检查 | 通过 |
| Rust Release 离线单元测试 | 7 项通过：乱序关联、迟到/断连清理、传输错误、坏帧及既有目录/图标测试 |
| C# Release 编译、压缩 sidecar publish | 通过；无新增依赖 |
| .NET Bridge/HostStop E2E | testhost 在用例启动前中止，0 项执行；不是测试通过 |
| 扫描边界、目录遍历、权限及 HostConnection 回归 | 尚未在本候选上执行成功；不可借用上游结果作为本轮通过记录 |
| 真实 stdio 协议辅助探针 | v1.8.1 两 Bridge 与候选单 Bridge 均能在长轮询期间响应普通请求；独立 transportId 与业务 requestId 均核对，使用中文临时路径 |
| 父进程退出/关闭 stdin 辅助探针 | 修正前超时，stderr 显示监视错误 5；修正后两种退出路径均为 Bridge 退出码 0、所属 Host 停止，另一临时库的 Host 保持运行 |
| 桌面 Release 编译 | 通过，独立测试 identifier `com.gamelibrary.desktop.rg02test`，`--no-bundle`，没有生成安装器；Vite 保留既有大 chunk 警告 |
| 桌面启动/整棵进程树对比 | 当前 v1.8.2 在 Tauri setup 阶段报拒绝访问（os error 5），退出码 3221226505，未出现 CDP 目标；失败，未取得整棵树样本 |

VSTest 错误来自 `ProcessManager.OpenProcess` → `EnsureWatchingForExit` → `ProcessHelper.SetExitCallback`。原始 TRX 位于 `.tmp/rg02/v182/test-results/bridge-v182.trx`。未修改工具链、ACL、全局环境或安全策略。实际游戏运行、真实库扫描、安装与更新均未验证。

桌面错误日志为 `.tmp/rg02/v182/desktop-smoke.log`，构建日志为 `desktop-build.log`。失败发生于应用 setup，尚不能确定具体系统限制；不把它归为已排除的环境问题，也不将历史基线启动失败替代当前基线检查。此次仅启动测试 identifier，没有启动会与正式实例冲突的原版便携包。

## 占用测量边界

先用相同操作初始化独立空库，再预热 30 秒，采样 7 次，间隔目标 5 秒；CIM 查询使实际间隔约 6–7 秒。范围仅为 C# Bridge 与 Host，不含 Rust 桌面壳、WebView、游戏进程或父进程监视。各版本只有一轮，是探索性样本，不能作为整机或整个桌面应用的内存结论。

最终候选在桌面编译及退出探针全部结束后重新采样，样本见 [原始数值](2026-10-30-rg02-metrics.json)。相对同一 v1.8.1 基线的结果如下：

| 指标 | v1.8.1 基线 | 最终候选 |
| --- | ---: | ---: |
| Bridge 数量（不含 Host） | 2 | 1 |
| Private Bytes 中位数 | 122.65 MiB | 86.26 MiB |
| Working Set 求和中位数 | 288.65 MiB | 210.68 MiB |
| 长轮询期间普通请求 | 6.26ms | 21.95ms |
| 采样区间 CPU 累计增量 | 0.0625s / 40.68s | 0.03125s / 44.92s |

Private Bytes 下降 36.39 MiB（29.7%）。Working Set 含共享页重复计算，不代表独占物理内存。普通请求均在长轮询结束前返回；单次延迟和极少的 CPU 时间不足以判断性能提升。

初轮候选中位数为 85.28 MiB（约降 30.5%），采于父进程监视修正之前。修正后一次采样与桌面编译重叠，作为诊断保留，不纳入正式对比。各轮均未启用 `--parent-pid`；不能据此估计监视 fallback 的整机收益。初始化耗时不是桌面启动耗时，且受首次解压和缓存影响，不宣称启动加速。

任务日志均在 `.tmp/rg02/v182/`：正式辅助比较采用 `baseline-probe.log` 和 `candidate-verified.log`；初轮/重叠采样分别为 `candidate-probe.log`、`candidate-final-probe.log`。退出诊断与验证见 `exit-diagnostic2.log`、`exit-scoped.log` 和 `eof-verified.log`。探针收尾后再次检查，本 worktree 没有遗留 Bridge/Host 进程。

## 剩余验收与交付边界

仍需在可正常启动 VSTest/桌面的环境完成定向 IPC、权限、重连及扫描回归，验证桌面父进程退出、只清理所属 Host，以及整个桌面进程树的占用和启动时间。单元测试不能替代这些检查，辅助探针也不能替代桌面 E2E。若继续受环境限制，可运行任务目录中的 `manual-check.ps1`，它只针对当前 worktree 和合成库，并恢复调用进程的 TEMP/TMP。

该脚本使用 `--no-restore`，不安装或升级依赖；如果报缓存缺失，应先停下并确认依赖操作。自动验证仍有失败/未运行项，当前提交只是可审查的本地检查点，不是 v1.8.2 的发布验收结论。

压缩发布修正可单独回退，单 Bridge 改动也可单独回退；不涉及数据库迁移。main、正式安装和游戏数据均保留。实现提交只存本地，推送、合并、安装器与发布仍需另行授权。

本地检查点：`b1e6674` 为原 R1 发布修正，`8b006c6` 为单 Bridge 快照，`d4c6700` 合入正式 v1.8.1，`319fc48` 修正受限父进程监视及脚本参数。R3 的桌面/E2E 验收仍未完成，R4 的版本与文档登记只表示草稿准备完成。
