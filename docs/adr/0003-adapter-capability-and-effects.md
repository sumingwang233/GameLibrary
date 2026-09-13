# ADR-0003 适配器能力与外部副作用

- 状态：已接受
- 日期：2026-09-13（T20）

## 决策

1. 适配职责拆分为五个端口：`IToolLocator`（只读发现）、`IToolValidator`（版本/指纹校验）、`ILaunchAdapter`（生成纯数据 LaunchPlan，不启动进程）、`IVerificationRunner`（隔离样本验证）、`LaunchExecutor`（唯一进程执行器）。禁止巨型 IToolAdapter。
2. 能力分级固定五档：`Unknown / Unsupported / Guided / SemiAutomatic / VerifiedAutomatic`；每项子能力（canLaunch、canDeploy、canRollback、mayUseNetwork…）单独 supported/unsupported/unknown + 证据。
3. **技术能力与授权正交**：Verification 是能力证据，不是授权；授权由宿主 AccessPolicy 决定，绑定工具指纹与范围。
4. 外部工具副作用用 `ToolEffectDeclaration` 声明（observed/possible/unknown 写入、联网、回滚证据）；声明不冒充 OS 沙箱。
5. 翻译成功验收 = 游戏运行证据 + 工具关联证据 + 文本生效证据（按模式组合），进程存在/窗口出现不算。

## 理由

MTool/RenpyThief 是闭源第三方工具：能力未知比假装已知安全；授权与能力混在一个状态会导致“用户批准过”被误读为“已验证”。外部进程写入无法被本应用约束，白名单字段只会制造安全错觉。

## 被排除方案

- 单一 IToolAdapter 扫描+部署+注入+回滚：无法独立测试，职责不清。
- AllowedWriteRoots 冒充沙箱：外部进程不受约束。
- 以进程存在证明翻译生效：假阳性。

## 不可变约束

- 不自制注入器、不自动复制补丁、不执行扫描到的脚本、不猜工具参数。
- RenpyThief 未获实测协议前只能 Guided/Unknown。
- 适配器验证仅在用户允许的隔离样本执行。

## 迁移影响

工具自动更新/关键 DLL 替换使对应 Verified 记录失效，需重新验证。

## 验证用例

LA-03/05/06/07、S2 tool-adapter-matrix、翻译适配验证记录模板全字段。
