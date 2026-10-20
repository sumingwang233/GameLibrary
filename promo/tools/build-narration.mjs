// Regenerates the subtitle / narration table from config/copy.json so the document
// cannot drift from the cues that are actually burned in.
import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..");
const copy = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "copy.json"), "utf8"));
const film = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "film.json"), "utf8"));

const stamp = (seconds) => {
  const m = Math.floor(seconds / 60);
  const s = seconds - m * 60;
  return String(m).padStart(2, "0") + ":" + s.toFixed(1).padStart(4, "0");
};
const spokenChars = (text) => text.replace(/[，。、！？：；·\s]/g, "").length;

const lines = [];
lines.push("# 旁白稿与字幕表（v1.7.22 · 开发版演示）");
lines.push("");
lines.push("> 第一版**不含旁白配音**：本稿同时作为旁白文本、字幕文本与 SRT 来源。");
lines.push("> 若后续需要配音，按文末流程补录，不改动画面时间轴。");
lines.push("");
lines.push(`## 一、字幕 / 旁白稿（${film.film.duration} 秒版，共 ${copy.subtitles.length} 条）`);
lines.push("");
lines.push("| # | 入点 | 出点 | 文本 | 读速（字/秒） |");
lines.push("|---|---|---|---|---|");
copy.subtitles.forEach((cue, index) => {
  const rate = (spokenChars(cue.text) / Math.max(1, cue.end - cue.start)).toFixed(1);
  lines.push(
    `| ${index + 1} | ${stamp(cue.start)} | ${stamp(cue.end)} | ${cue.text} | ${rate} |`,
  );
});
lines.push("");
lines.push("- 字幕最多两行，最大宽度 1560px（1920 画布），字号 42px，底部安全边距 72px。");
lines.push("- 字幕不带句末句号；疑问、感叹保留。");
lines.push("- 字幕与关键按钮不重叠：镜头取景时把按钮留在字幕带上方。");
lines.push("- 片尾品牌信息在画面上部，与最后两条字幕不遮挡。");
lines.push("");
lines.push("## 二、旁白工程说明");
lines.push("");
lines.push("- 本版成片无旁白音轨；`deliverables/GameLibrary-promo.srt` 与上表保持一字不差。");
lines.push("- 旁白未录制的状态在检查报告中明确写出，不用静音文件冒充配音。");
lines.push("");
lines.push("### 若后续补录");
lines.push("");
lines.push("1. 按上表逐条录制，单条留头 0.2s、尾 0.3s 余量。");
lines.push(
  `2. 读法：GameLibrary 连读为「Game Library」；Unity 读「Unity」；MTool 读字母 M + Tool。`,
);
lines.push("3. 混音：旁白轨单独一条，音乐在旁白区间自动压低约 6 dB；音效不高于旁白 −12 dB。");
lines.push("4. 生成后重跑 `node audio/mix.mjs audio/final-plan.json`，并在检查报告里补「已试听」结论。");
lines.push("");
fs.writeFileSync(path.join(ROOT, "docs", "narration-and-subtitles.md"), lines.join("\n"));
console.log(`[narration] ${copy.subtitles.length} cues`);
