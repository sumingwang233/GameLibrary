# GameLibrary 代码审查报告

## 审查元数据

| 项目 | 值 |
|---|---|
| 工具 | `open-code-review` / `ocr v1.12.9` |
| Provider / Model | `deepseek` / `deepseek-flash` |
| 审查范围 | `489a6c9..84a99bc` |
| 选中审查文件 | 1 个 |
| 排除文件 | 3 个 Markdown（`unsupported_ext`） |
| OCR 会话 | `ad8387e3-f99c-47fd-b53a-3d1650f54cbc` |
| 结果 | 5 条：1 high、4 medium |

本次是基于 Git 差异的增量审查，不代表 263 个源码文件都被 OCR 重新扫描。范围内的 4 个新增文件中，OCR 只选择了 `.github/workflows/code-review-graph.yml`；`docs/code-review-graph/*.md` 因扩展名被排除。`.mimosa/`、`.qoder-credits/` 等本地未跟踪审计产物没有纳入审查。

## High

### H-01 第三方 GitHub Action 使用可变 tag

- **位置**：`.github/workflows/code-review-graph.yml:17`
- **类别**：security
- **问题**：`tirth8205/code-review-graph@v2.3.9` 是可移动 tag。上游 tag 被重指向或账号被接管后，PR 工作流会执行未经审查的新代码；该 Job 同时拥有 `pull-requests: write` 权限。
- **建议**：改为完整 40 位 commit SHA，并保留版本注释，例如 `tirth8205/code-review-graph@<full-sha> # v2.3.9`。升级 action 时走单独 PR 并重新审查。

## Medium

### M-01 工作流没有执行超时

- **位置**：`.github/workflows/code-review-graph.yml:13`
- **类别**：other
- **问题**：网络、Registry 或第三方 action 卡住时，Job 可能占用 GitHub runner 到默认上限。
- **建议**：在 Job 级别增加 `timeout-minutes`，按该图谱任务的实际耗时设置 10–15 分钟，并在超时后保留失败状态供排查。

### M-02 Pull Request 没有并发取消策略

- **位置**：`.github/workflows/code-review-graph.yml:3-4`
- **类别**：other
- **问题**：同一 PR 快速推送多次时，旧的图谱审查仍会继续运行，造成重复消耗和评论竞态。
- **建议**：增加基于 `${{ github.workflow }}-${{ github.ref }}` 的 `concurrency` group，并设置 `cancel-in-progress: true`。

### M-03 checkout 默认浅克隆可能缺少 PR 基线

- **位置**：`.github/workflows/code-review-graph.yml:15`
- **类别**：bug
- **问题**：`actions/checkout` 默认 `fetch-depth: 1`。如果 `code-review-graph` action 需要 merge-base、PR base 或提交历史来计算影响范围，浅克隆会让分析失败或不完整。
- **建议**：确认 action 文档的历史依赖；如果需要基线，设置 `fetch-depth: 0`，或至少 fetch PR base SHA。

### M-04 checkout 版本与仓库现有 CI 不一致

- **位置**：`.github/workflows/code-review-graph.yml:15`
- **类别**：maintainability
- **问题**：新增工作流使用 `actions/checkout@v7`，现有 `.github/workflows/ci.yml` 两处使用 `actions/checkout@v5`。若 `v7` 尚未发布或不可解析，工作流会在首步直接失败；即使可用，也增加了版本维护分叉。
- **建议**：先确认 `v7` 的确存在，再统一仓库版本；更稳妥的做法是统一使用经过验证的完整 commit SHA。

## 优先级

1. **立即处理 H-01**：固定第三方 action 的 commit SHA，并复核 `pull-requests: write` 是否确实需要。
2. **随后处理 M-01、M-02**：增加超时和并发取消，避免 runner 与 PR 评论资源浪费。
3. **在首次 PR 运行前处理 M-03、M-04**：确认历史深度和 `actions/checkout` 版本，确保工作流能稳定启动并拿到正确基线。

## 验证记录

- `ocr llm test`：连接成功。
- OCR：`Review complete: 5 finding(s) across 1 selected item(s)`。
- OCR 工具调用失败数：0。
- `code-review-graph` 已同步到 `84a99bc`，图谱状态为 `head_matches_build=true`。
- 本次未自动修改代码，也未执行构建或测试；审查目标是 CI 工作流差异。
