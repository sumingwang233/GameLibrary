# ADR-0005 正交状态集

- 状态：已接受
- 日期：2026-09-13（T20）

## 决策

领域状态拆成互相正交的维度，禁止合并成单一 UserStatus 大枚举：

- **CandidateKind**（物理类型）：Unknown/Container/GameRoot/FileGame/NestedCandidate/Tool/SupportDirectory。
- **CandidateReviewState**（审核状态）：observed → stabilizing → pendingReview → accepted/deferred/ignored。
- **GameAvailability**（路径可用性）：unknown/available/suspectedMissing/missing/offline/accessError/rootUnbound；missing 需卷在线且两次间隔≥60s 的成功完整核对仍缺失。
- **GameMembership**（成员关系）：active/removed；removed 是库内移除+忽略记录，不是磁盘删除。
- 提示类（DuplicateHint/BackupHint/BrokenEntry）是独立诊断标记，不占类型位。
- UI 状态由 membership + availability + profile 校验**计算**得出，不落库。

`translationRequirement` 是独立策略字段（Auto/Required/NotRequired + 来源），与显示标签分离；删除标签不改变策略。

## 理由

单一枚举会产生组合爆炸且互相污染：候选“离线”与“被忽略”是两个问题；accepted 候选不因离线回退为 new。翻译标签若控制启动，删标签会悄悄绕过工具（策划案 7.4）。

## 被排除方案

- 一个 State 字段装所有概念：状态迁移图不可测试。
- 用标签驱动启动策略：标签是显示层资料。

## 不可变约束

- 第一次缺失只到 suspectedMissing；Root Offline/AccessDenied/取消/扫描未完成不参与 Missing 推断。
- Missing 不删除 Game、图片、自定义资料。
- 翻译需求只对 GameRoot/FileGame/NestedCandidate 生效。

## 迁移影响

状态字段持久化为固定英文枚举值；已发布值不得无版本重命名（契约治理）。

## 验证用例

FS-02/03、ID-04/05、NW-04、MD-02、状态机单元测试（T04）。
