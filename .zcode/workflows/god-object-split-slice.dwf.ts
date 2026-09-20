/* zcode-workflow
description: God Object 拆分单片执行：审计选域 → 独立复核 → 实施 → format/build/test 三门禁（含返修循环），产出方案与执行报告工件。
whenToUse: GameLibrary 的 OperationDispatcher/SqliteLibraryStore God Object
  拆分路线图推进：每拆一片调用一次，传 sliceNo（第几片）与 candidates（本轮候选域及耦合提示）。前置依赖：仓库在
  D:\Official\GameLibrary、SDK 在用户目录、CI 与门禁命令已就绪。
args:
  sliceNo:
    type: number
    description: 第几片（数字，用于命名与提示词，如 8）
    required: true
  candidates:
    type: string
    description: 本轮候选域与特殊耦合提示，一行中文（如「备份域 Backups.cs、Cataloging 剩余（注意 GameDto
      与批量充实共用）；扫描域注意 JobManager 耦合」）
    required: true
  focusNotes:
    type: string
    description: 本轮额外注意事项（可选，中文；会附在审计提示词末尾）
    required: false
*/
interface AreaInventory {
  /** 业务领域名。 */
  area: string;
  /** 所在分部文件，workspace 相对路径。 */
  file: string;
  /** 该领域在分发器中的处理方法数。 */
  methodCount: number;
  /** 依赖的共享状态成员。 */
  stateDependencies: string[];
  /** 对应测试文件与覆盖情况，一句话。 */
  testCoverage: string;
}

interface FirstSlice {
  /** 这一片的标题，一句话。 */
  title: string;
  /** 新建文件清单：路径 + 职责。 */
  newFiles: { path: string; purpose: string }[];
  /** 要迁移的方法：真实方法名 + 来源文件。 */
  methodsToMove: { name: string; from: string }[];
  /** 分发器如何委托（switch 分发保持原位的说明）。 */
  delegation: string;
  /** 必须保持不变的行为点（响应字段、错误码、事件名、幂等语义）。 */
  invariants: string[];
  /** 这一片的主要风险与对策。 */
  risks: string[];
}

interface SplitPlan {
  /** 目标领域盘点。 */
  inventory: AreaInventory[];
  /** 本次实施的这一片。 */
  firstSlice: FirstSlice;
  /** 后续拆分顺序（本次不实施）。 */
  order: string[];
  /** 被否决的替代方案与原因。 */
  discardedAlternatives: string;
}

interface PlanReview {
  /** 方案是否可直接实施。 */
  approved: boolean;
  /** 会出问题的地方：文件/方法 + 会怎么坏。 */
  problems: { where: string; what: string }[];
  /** 方案缺失的东西。 */
  missing: string[];
}

interface ImplOutcome {
  /** 改动过的文件，workspace 相对路径。 */
  filesChanged: string[];
  /** 做了什么，中文两三句。 */
  summary: string;
  /** 实施中发现并处理的偏差及原因（无偏差则空字符串）。 */
  deviations: string;
}

interface Finding {
  /** 位置：文件:行号 或 阶段名。 */
  where: string;
  /** 一句话：发现了什么。 */
  what: string;
  /** 证据：读过的行、或命令与输出。 */
  evidence: string;
  /** verified=门禁或复核确认；unconfirmed=未复核。 */
  status: "verified" | "unconfirmed";
  /** low/medium/high；high 仅留给会导致错误结果的项。 */
  severity: "low" | "medium" | "high";
}

interface WorkflowReport {
  /** 两三句回答用户要的事。 */
  conclusion: string;
  findings: Finding[];
  /** 本次检查了什么、怎么检查的。 */
  verified: string[];
  /** 没检查什么、为什么。 */
  notCovered: string[];
}

const sliceNo = Number(args.sliceNo);
const ordinal = `第${sliceNo}片`;
const candidates = String(args.candidates);
const focusNotes = args.focusNotes === undefined ? "" : String(args.focusNotes);

artifact.board("progress", {
  title: `拆分进度（${ordinal}）`,
  key: "step",
  status: "stage",
  columns: ["审计", "复核", "实施", "门禁"],
});

function tail(text: string): string {
  return text.length > 4000 ? text.slice(text.length - 4000) : text;
}

phase("审计并选定目标域");
const auditor = agent("结构审计员", {
  system:
    "你是资深 .NET 架构审计员，服务 GameLibrary 仓库（工作目录即仓库根）。遵循仓库 AGENTS.md：证据优先，结论落到文件与行号；区分事实、推断、建议；中文输出（路径/方法名保持英文）。你是只读角色：不得修改、创建或删除任何文件。",
});
let plan = await auditor.ask<SplitPlan>(
  [
    `任务：为 OperationDispatcher/SqliteLibraryStore God Object 拆分的${ordinal}选定目标域并产出方案（只产出方案，不改任何文件）。`,
    "",
    "背景：既有拆分模式固定——internal sealed 处理器（Host.Hosting 下）+ 构造注入域内实际使用的依赖（store 经 Func<SqliteLibraryStore?> 每请求取值，其余按需，不多不少）+ switch 臂委托 + 复用 IpcRequests 助手。已拆出的 Handler 都在 src/GameLibrary.Host/Hosting/ 下，先 ls 了解现状；历史条目在 docs/progress.md（R49 起），含既判（如 ignores.remove 的 expectedRevision 既有矛盾不得顺手改）。",
    "",
    `本轮候选域（以你的证据在候选中选定，也可提出更优目标并说明理由）：${candidates}`,
    ...(focusNotes.length > 0 ? ["", `本轮注意事项：${focusNotes}`] : []),
    "",
    "步骤：",
    "1. 读仓库根 AGENTS.md；读 docs/progress.md 相关条目了解既定模式与既判。",
    "2. 对各候选域盘点：处理方法与 switch 臂数、共享状态依赖面、私有助手及其跨文件引用（grep 全仓，含已拆出的全部 handler）、测试覆盖（tests/ 下对应目录）。",
    "3. 对选定域产出文件级方案：新建文件、要迁移的方法（真实方法名，grep 核对）、委托方式、契约不变量（contracts/operations.v1.json 声明、响应字段、错误码、事件名、幂等语义）、风险与对策。",
    "4. order 更新后续顺序（直至两个 God Object 拆完的终态），discardedAlternatives 说明落选候选与原因。",
    "",
    "所有输出字段值用中文（路径/方法名保持英文），结论给文件:行号证据。",
  ].join("\n"),
);
report(
  { step: "结构审计", stage: "审计", note: `选定：${plan.firstSlice.title}；迁移 ${plan.firstSlice.methodsToMove.length} 个方法` },
  "progress",
);
log(`审计完成：${plan.firstSlice.title}`);

phase("换人复核拆分方案");
const reviewer = agent("方案复核员", {
  system:
    "你是独立方案复核员，没看过方案的产出过程。职责是挑毛病而不是背书：批准需要证据，反对是更容易的动作。只读：不得修改任何文件；结论落到文件与行号。中文输出。",
});
let review = await reviewer.ask<PlanReview>(
  [
    "仓库 D:\\Official\\GameLibrary。以下是「God Object 拆分方案」（JSON），请打开方案涉及的真实文件核对，只读不改：",
    JSON.stringify(plan),
    "",
    "核对点：",
    "- 方法与助手清单是否真实、完整？grep 确认没有漏掉该域必需一起迁移的私有助手；确认被迁移助手没有被其他分部文件或已拆出的 handler 引用（若被引用，方案是否正确处理）。",
    "- 共享状态依赖是否准确完整？构造注入清单是否与域内实际使用一一对应（不多不少）？新文件 using 清单是否覆盖方法体全部引用？",
    "- 契约不变量是否列全（operations.v1.json 对该域操作的参数/幂等键声明、响应字段、错误码、事件名）？",
    "- 与已拆出的处理器模式是否同构？",
    "- 与 tests/GameLibrary.ArchitectureTests/DependencyDirectionTests.cs 的依赖方向规则是否冲突？",
    "",
    "按 JSON 返回 approved / problems / missing，值用中文，每条问题给文件:行号证据。",
  ].join("\n"),
);
report(
  { step: "方案复核", stage: "复核", note: review.approved ? "方案通过复核" : `复核提出 ${review.problems.length} 个问题` },
  "progress",
);
log(review.approved ? "方案通过复核" : `复核提出 ${review.problems.length} 个问题`);

if (!review.approved) {
  phase("按复核意见修订方案");
  plan = await auditor.ask<SplitPlan>(
    [
      "复核意见如下，请修订方案（只改需要改的部分，其余保持原样返回）：",
      `问题：${JSON.stringify(review.problems)}`,
      `缺失：${JSON.stringify(review.missing)}`,
      `原方案：${JSON.stringify(plan)}`,
    ].join("\n"),
  );
  review = await reviewer.ask<PlanReview>(
    `修订后的方案（JSON）：\n${JSON.stringify(plan)}\n\n请再次按同样的核对点复核，返回 approved / problems / missing。`,
  );
  report(
    {
      step: "方案修订复核",
      stage: "复核",
      note: review.approved ? "修订后通过" : `修订后仍有 ${review.problems.length} 个问题，带着已知问题实施`,
    },
    "progress",
  );
}

phase("实施拆分");
const implementer = agent("拆分实施员", {
  system:
    "你是资深 .NET 重构工程师，在 GameLibrary 仓库实施一次小步拆分。硬约束：对外契约零变化（响应字段、错误码、事件名、幂等语义原样）；TreatWarningsAsErrors 生效，要求 0 警告 0 错误；不得修改既有测试的断言；不运行任何 git 提交类命令（提交由主会话完成）；不运行全量测试（脚本会在你之后统一跑 format/build/test 三个门禁）。SDK：C:/Users/sumingwang/.dotnet-sdk-10.0/dotnet.exe。环境硬规则：Git Bash 下严禁 `> nul` 重定向（会在仓库根创建名为 nul 的文件）——丢弃输出一律用 `> /dev/null`。若约束互相矛盾或某个门禁不可能通过，直接上报说明，不要绕过。中文输出。",
});
let impl = await implementer.ask<ImplOutcome>(
  [
    `按以下${ordinal}拆分方案实施（方案已经过独立复核）：`,
    JSON.stringify(plan.firstSlice),
    "",
    "补充要求：",
    "- 与已拆出的处理器同构：新类放 src/GameLibrary.Host/Hosting/ 下，internal sealed；构造注入以复核过的方案为准——只注入域内实际使用的依赖（未使用字段会触发 CS0414 破坏 0 警告门禁）；switch 分发保持原位改委托；复用 IpcRequests 助手，不要复制实现。",
    "- XML 文档注释用中文，风格对齐已拆出的 Handler。类头注释的 partial 清单若涉及本片迁移的文件，同步更新（纯注释）。",
    "- 写完后可运行 dotnet format 自动整理；可跑单项目快速构建自查（dotnet build src/GameLibrary.Host/GameLibrary.Host.csproj -c Debug）。",
    "- 若方案与实际代码冲突，按最小偏差实施并在 deviations 里写明。",
    "",
    "返回 JSON：filesChanged / summary（中文两三句）/ deviations（无则空字符串）。",
  ].join("\n"),
);
report({ step: "拆分实施", stage: "实施", note: impl.summary }, "progress");
log(`实施完成：${impl.filesChanged.length} 个文件改动`);

phase("运行门禁：format、Release 构建、全量测试");
const gateResults: { name: string; passed: boolean; detail: string }[] = [];
let allGreen = false;
for (let round = 1; round <= 3; round++) {
  const format = await world.run("C:/Users/sumingwang/.dotnet-sdk-10.0/dotnet.exe", ["format", "--verify-no-changes"], { timeoutMs: 300000 });
  if (format.exitCode !== 0) {
    gateResults.push({ name: `format（第 ${round} 轮）`, passed: false, detail: tail(format.stdout + format.stderr) });
    impl = await implementer.ask<ImplOutcome>(
      `dotnet format --verify-no-changes 失败（第 ${round} 轮）。输出尾部：\n${tail(format.stdout + format.stderr)}\n\n请修复后返回同样的 JSON（filesChanged / summary / deviations）。保持行为与契约不变。`,
    );
    continue;
  }

  const build = await world.run("C:/Users/sumingwang/.dotnet-sdk-10.0/dotnet.exe", ["build", "-c", "Release", "--nologo"], { timeoutMs: 600000 });
  if (build.exitCode !== 0) {
    gateResults.push({ name: `Release 构建（第 ${round} 轮）`, passed: false, detail: tail(build.stdout + build.stderr) });
    impl = await implementer.ask<ImplOutcome>(
      `dotnet build -c Release 失败（第 ${round} 轮）。输出尾部：\n${tail(build.stdout + build.stderr)}\n\n请修复后返回同样的 JSON。保持行为与契约不变。`,
    );
    continue;
  }

  const test = await world.run("C:/Users/sumingwang/.dotnet-sdk-10.0/dotnet.exe", ["test", "-c", "Release", "--no-build", "--nologo"], { timeoutMs: 900000 });
  if (test.exitCode !== 0) {
    gateResults.push({ name: `全量测试（第 ${round} 轮）`, passed: false, detail: tail(test.stdout + test.stderr) });
    impl = await implementer.ask<ImplOutcome>(
      `dotnet test -c Release 失败（第 ${round} 轮）。输出尾部：\n${tail(test.stdout + test.stderr)}\n\n请修复后返回同样的 JSON。注意：不得修改既有测试的断言；若失败与本次拆分无关，在 deviations 里给出证据。`,
    );
    continue;
  }

  gateResults.push({ name: "format + Release 构建 + 全量测试", passed: true, detail: test.stdout.split("\n").filter((l) => l.includes("已通过!")).join(" | ") });
  allGreen = true;
  break;
}
report(
  { step: "门禁验证", stage: "门禁", note: allGreen ? "format、构建、全量测试全绿" : "三轮后仍有门禁未通过" },
  "progress",
);
log(allGreen ? "门禁全绿" : "门禁未全绿");

phase("汇总产出方案文档");
const findings: Finding[] = [];
if (!review.approved) {
  for (const problem of review.problems) {
    findings.push({
      where: problem.where,
      what: problem.what,
      evidence: "方案复核员指出，未经二次实证",
      status: "unconfirmed",
      severity: "medium",
    });
  }
}
if (impl.deviations.length > 0) {
  findings.push({
    where: "拆分实施",
    what: "实施与方案存在偏差",
    evidence: impl.deviations,
    status: "unconfirmed",
    severity: "low",
  });
}
for (const gate of gateResults.filter((g) => !g.passed)) {
  findings.push({
    where: gate.name,
    what: "门禁失败",
    evidence: gate.detail,
    status: "verified",
    severity: "high",
  });
}

const markdown = [
  `# God Object 拆分${ordinal}——方案与执行报告`,
  "",
  "## 目标域选定",
  ...plan.inventory.map(
    (a) => `- **${a.area}**（${a.file}，${a.methodCount} 个方法）— 共享状态：${a.stateDependencies.join("、")}；测试：${a.testCoverage}`,
  ),
  "",
  "## 本次实施",
  `**${plan.firstSlice.title}**`,
  "",
  "新建文件：",
  ...plan.firstSlice.newFiles.map((f) => `- \`${f.path}\` — ${f.purpose}`),
  "",
  "迁移的方法：" + plan.firstSlice.methodsToMove.map((m) => `${m.name}（自 ${m.from}）`).join("、"),
  "",
  "委托方式：" + plan.firstSlice.delegation,
  "",
  "保持不变的行为点：",
  ...plan.firstSlice.invariants.map((i) => `- ${i}`),
  "",
  "## 后续拆分顺序（本次未实施）",
  ...plan.order.map((s, i) => `${i + 1}. ${s}`),
  "",
  "## 落选候选与原因",
  plan.discardedAlternatives,
  "",
  "## 执行结果",
  `- 改动文件：${impl.filesChanged.join("、")}`,
  `- 说明：${impl.summary}`,
  `- 偏差：${impl.deviations.length > 0 ? impl.deviations : "无"}`,
  ...gateResults.map((g) => `- ${g.passed ? "✅" : "❌"} ${g.name}${g.passed ? "" : "：" + g.detail.slice(0, 500)}`),
].join("\n");
await artifact.markdown("split-plan", markdown, {
  title: `God Object 拆分${ordinal}执行报告`,
  description: "目标域盘点与选定、实施明细与门禁结果",
  primary: true,
});

const result: WorkflowReport = {
  conclusion: allGreen
    ? `${ordinal}拆分完成：「${plan.firstSlice.title}」。方案经独立复核，format、Release 构建（0 警告 0 错误）与全量测试全部通过；改动未提交，等主会话审阅后提交。`
    : `${ordinal}拆分实施完毕，但三轮返修后门禁仍未全绿，代码处于未通过门禁的状态，需要人工介入后再提交。`,
  findings,
  verified: [
    "方案由独立复核员对照真实文件核对过方法/助手清单、共享状态依赖与契约不变量",
    ...(allGreen
      ? [
          "dotnet format --verify-no-changes 通过（world.run 退出码 0）",
          "dotnet build -c Release 通过，0 警告 0 错误",
          "dotnet test -c Release 全量通过：" + (gateResults.find((g) => g.passed)?.detail ?? ""),
        ]
      : []),
  ],
  notCovered: [
    "后续拆分顺序未实施，本报告只完成本片",
    "发布打包链路未运行——纯结构重构预期无影响，发布前由打包门禁覆盖",
  ],
};
return result;