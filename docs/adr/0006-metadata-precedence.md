# ADR-0006 资料优先级与来源证据

- 状态：已接受
- 日期：2026-09-13（T20）

## 决策

1. 每个资料字段两层存储：`AutoValue`（自动提取，含 SourceKind/SourcePath/Evidence/Confidence/DetectorVersion）+ `UserMode`（Auto/Set/Clear）+ `UserValue`。展示值 = Set/Clear 优先，否则 AutoValue。null 不承担“用户清空”语义。
2. 名称优先级：用户锁定 Set > 已核验本地清单/引擎标题 > 目录名 > EXE 产品名 > 文件名。
3. 自动刷新只更新 AutoValue 与本检测器来源记录；用户层与标签 Suppress 永不被后台事务覆盖。
4. 标签多来源共存（路径祖先、检测器各自记录来源）；自动重扫只替换本检测器的记录。
5. v1 无网络元数据、无 LLM；生成简介仅规则提取与事实模板，允许“暂无剧情简介”。

## 理由

“编辑后反复扫描用户修改保留”（MD-01）与“清空生效不被恢复”（MD-02）要求两层存储；否则 null 语义冲突、后台刷新与用户编辑互相踩踏。来源证据是“为什么显示这个值”的唯一解释链。

## 被排除方案

- 单值字段 + 时间戳比较（last-write-wins）：并发扫描覆盖用户输入。
- 从文件夹名/引擎推断剧情简介：编造事实。

## 不可变约束

- 文本读取限制（单文件 1 MiB、单游戏 5 MiB、受控编码尝试）；乱码不当有效简介。
- 不执行任何引擎/脚本代码求值（含 options.rpy 表达式、package.json scripts）。

## 迁移影响

DetectorVersion 变化仅使旧建议降级待刷新，不动用户层。

## 验证用例

MD-01/02/03/04、FS-02（来源标签）、metadata.preview/refresh 契约。
