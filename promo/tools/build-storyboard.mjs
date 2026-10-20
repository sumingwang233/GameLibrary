// Emits promo/docs/storyboard.md from the real shot list so the document can never
// drift from what the renderer actually plays.
import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..");
const shots = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "shots.json"), "utf8"));
const copy = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "copy.json"), "utf8"));
const film = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "film.json"), "utf8"));

const SEGMENTS = [
  ["A", 0, 12, "问题：散落的文件夹"],
  ["B", 12, 38, "收进游戏库：目录 → 扫描 → 审核 → 入库 → 启动方式"],
  ["C", 38, 60, "找到想玩的：搜索 → 收藏 → 标签 → 详情"],
  ["D", 60, 78, "看懂游戏名：单个翻译 → 译名 → 切回原名 → 批量入口"],
  ["E", 78, 105, "Unity 插件：服务前提 → 触发 → 流程图 → 配置状态"],
  ["F", 105, 122, "MTool 接入：工具目录 → 生成启动脚本 → 启动方式"],
  ["G", 122, 140, "回到游戏：完整库 → 收藏 / 标签 → 启动操作"],
  ["H", 140, 150, "下载指引：片尾卡"],
];

const fmt = (n) => n.toFixed(1) + "s";
const subtitleAt = (t) => copy.subtitles.find((s) => t >= s.start && t < s.end);

const lines = [];
lines.push("# GameLibrary 宣传片 · 分镜时间表");
lines.push("");
lines.push(`- 版本：\`${film.product.versionLabel}\`（${film.product.buildType}）`);
lines.push(`- 规格：${film.film.duration}s / ${film.film.width}×${film.film.height} / ${film.film.fps}fps`);
lines.push("- 本表由 `promo/tools/build-storyboard.mjs` 从 `promo/config/shots.json` 生成，改镜头后重新生成即可。");
lines.push("");
lines.push("| 段落 | 时间 | 镜头 | 画面 | 屏幕注释 | 字幕 |");
lines.push("| --- | --- | --- | --- | --- | --- |");

for (const [id, t0, t1, title] of SEGMENTS) {
  const chunks = shots.chunks.filter((c) => c.t1 > t0 + 0.01 && c.t0 < t1 - 0.01);
  let first = true;
  for (const chunk of chunks) {
    const visual = chunk.scene ? `概念画面：\`${chunk.scene}\`` : `产品画面：\`${chunk.plate}\``;
    const sub = subtitleAt((chunk.t0 + chunk.t1) / 2);
    const label = chunk.label ? chunk.label.text : "";
    lines.push(
      `| ${first ? id + " · " + title : ""} | ${fmt(chunk.t0)}–${fmt(chunk.t1)} | ${chunk.id} | ${visual} | ${label} | ${first ? (sub ? sub.text : "") : ""} |`,
    );
    first = false;
  }
}

lines.push("");
lines.push("## 字幕全表");
lines.push("");
lines.push("| # | 时间 | 文案 |");
lines.push("| --- | --- | --- |");
copy.subtitles.forEach((s, i) => {
  lines.push(`| ${i + 1} | ${fmt(s.start)}–${fmt(s.end)} | ${s.text} |`);
});
lines.push("");
lines.push("## 屏幕注释（合规标注）");
lines.push("");
for (const chunk of shots.chunks) {
  if (!chunk.label) continue;
  lines.push(`- \`${chunk.id}\` ${fmt(chunk.t0)}：${chunk.label.text}`);
}
lines.push("");
fs.writeFileSync(path.join(ROOT, "docs", "storyboard.md"), lines.join("\n"));
console.log(`[storyboard] ${shots.chunks.length} shots, ${copy.subtitles.length} cues`);

