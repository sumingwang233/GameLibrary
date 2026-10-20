import fs from "node:fs";
import path from "node:path";

const ROOT = path.dirname(import.meta.dirname);

const subtitles = [
  { start: 0.9, end: 5.4, text: "下载过的游戏，散落在不同的文件夹里" },
  { start: 6.0, end: 10.2, text: "想玩的时候，却常常要先找一遍" },
  { start: 12.2, end: 15.0, text: "深色或浅色，按自己的习惯来" },
  { start: 15.8, end: 21.8, text: "用 GameLibrary，把它们整理到一处" },
  { start: 22.6, end: 29.0, text: "添加游戏所在的文件夹，扫描后逐项审核" },
  { start: 29.6, end: 34.8, text: "确认入库，下一次直接从游戏库找到它" },
  { start: 35.4, end: 40.8, text: "名称和启动方式，都能在详情里核对" },
  { start: 41.4, end: 45.0, text: "记得名字，就搜索" },
  { start: 45.6, end: 48.2, text: "常玩的，加入收藏" },
  { start: 48.8, end: 53.6, text: "想按自己的习惯分类，就给它们加上标签" },
  { start: 54.2, end: 58.4, text: "整理之后，找游戏不必再从文件夹开始" },
  { start: 58.8, end: 62.6, text: "遇到不熟悉的外文游戏名，可以翻译为中文" },
  { start: 63.0, end: 66.8, text: "点击之后，译名出现在标题位置" },
  { start: 67.2, end: 70.4, text: "原名仍然保留，需要时随时切换" },
  { start: 70.8, end: 73.6, text: "也可以一次处理多个游戏" },
  { start: 74.0, end: 79.2, text: "译名只改变游戏库里的显示" },
  { start: 82.4, end: 87.0, text: "准备游戏翻译，也能少一些手动操作" },
  { start: 87.6, end: 92.0, text: "设置好翻译服务后，可以自动配置翻译插件" },
  { start: 92.6, end: 98.2, text: "兼容检查、插件准备和配置过程，都有状态反馈" },
  { start: 98.8, end: 103.4, text: "配置完成，游戏内效果需要启动后验证" },
  { start: 104.2, end: 109.4, text: "使用 MTool 的游戏，也可以接入同一个启动流程" },
  { start: 110.0, end: 114.6, text: "设置工具目录，按支持情况准备启动方式" },
  { start: 115.2, end: 120.6, text: "从整理、查找到名称翻译和工具配置，这些准备都在一处" },
  { start: 126.2, end: 131.4, text: "打开游戏之前，少一点来回折腾" },
  { start: 132.6, end: 139.6, text: "GameLibrary，把时间留给游戏" },
  { start: 140.4, end: 145.6, text: "前往项目页面，查看版本说明并下载" },
];

const master = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "copy.json"), "utf8"));
master.brand.versionChip = "v1.7.22 · 开发版演示";
master.brand.tagline = "把散落的游戏整理到一处，把准备工作交给工具，把时间留给游戏。";
master.subtitles = subtitles;
master.annotations = {};
fs.writeFileSync(path.join(ROOT, "config", "copy.json"), JSON.stringify(master, null, 2) + "\n");

const chunk = (id, t0, t1, extra) => ({ id, t0, t1, ...extra });
const cam = (x, y, size) => [x, y, size];

const chunks = [
  chunk("A1", 0, 11.6, { scene: "problem" }),
  chunk("T0", 11.6, 15.2, {
    scene: "theme-toggle", fadeIn: 0.4,
    label: { kind: "chapter", text: "深色 / 浅色界面", from: 0.3, to: 3.4 },
  }),
  chunk("B1", 15.2, 18.2, {
    scene: "folder-panel", fadeIn: 0.35,
    label: { kind: "chapter", text: "选择文件夹 · 流程演示", from: 0.2, to: 2.8 },
  }),
  chunk("B2", 18.2, 20.4, {
    plate: "b-roots-before", cam: cam(0, 0, 1), fadeIn: 0.3,
    cursor: [{ t: 1.2, box: "添加游戏库", click: true }],
  }),
  chunk("B3", 20.4, 22.2, {
    plate: "b-roots-after", cam: cam(0, 0, 0.98), fadeIn: 0.35,
    cursor: [{ t: 1.3, box: "扫描游戏库", click: true }],
  }),
  chunk("B4", 22.2, 25.6, { plate: "b-scanning", cam: cam(0, 0, 1), fadeIn: 0.3 }),
  chunk("B5", 25.6, 29.2, {
    plate: "b-review", cam: cam(0, 0, 1), fadeIn: 0.35,
    cursor: [{ t: 0.9, box: "全选已加载项目", click: true }],
  }),
  chunk("B6", 29.2, 32.4, {
    plate: "b-review-selected", cam: cam(0, 0, 1), fadeIn: 0.3,
    cursor: [{ t: 1.1, box: "加入", click: true }],
  }),
  chunk("B7", 32.4, 35.6, {
    plate: "b-library-after", cam: cam(0, 0, 1), fadeIn: 0.45,
    label: { kind: "tag", text: "示例游戏库 · 演示数据", at: [0.302, 0.075], from: 0.5, to: 2.9 },
  }),
  chunk("B8", 35.6, 38.0, {
    plate: "b-detail-launch", cam: cam(0.44, 0, 0.8), camTo: cam(0.45, 0.02, 0.78), fadeIn: 0.35,
  }),
  chunk("B9", 38.0, 41.0, { plate: "b-detail-launch-bottom", cam: cam(0.55, 0.55, 0.45), fadeIn: 0.3 }),
  chunk("C1", 41.0, 45.4, { plate: "c-library", cam: cam(0, 0, 1), fadeIn: 0.4 }),
  chunk("C2", 45.4, 48.4, { plate: "c-search", cam: cam(0.02, 0.05, 0.96), fadeIn: 0.3 }),
  chunk("C3", 48.4, 51.4, { plate: "c-fav", cam: cam(0, 0, 0.99), fadeIn: 0.3 }),
  chunk("C4", 51.4, 54.4, { plate: "c-favlist", cam: cam(0, 0, 0.99), fadeIn: 0.3 }),
  chunk("C5", 54.4, 57.8, { plate: "c-tag", cam: cam(0, 0, 0.99), fadeIn: 0.3 }),
  chunk("C6", 57.8, 61.8, {
    plate: "c-detail", cam: cam(0.44, 0, 0.8), camTo: cam(0.45, 0.02, 0.78), fadeIn: 0.35,
  }),
  chunk("D1", 61.8, 64.4, {
    plate: "d-detail", cam: cam(0.44, 0, 0.8), fadeIn: 0.3,
    cursor: [{ t: 1.0, box: "翻译为简体中文", click: true }],
  }),
  chunk("D2", 64.4, 67.0, { plate: "d-processing", cam: cam(0.44, 0, 0.8) }),
  chunk("D3", 67.0, 70.6, {
    plate: "d-translated", cam: cam(0.44, 0, 0.8), fadeIn: 0.3,
    label: { kind: "tag", text: "示例数据（翻译结果）", at: [0.805, 0.052], from: 0.4, to: 3.2 },
  }),
  chunk("D4", 70.6, 73.8, {
    plate: "d-batch", cam: cam(0, 0, 0.99), fadeIn: 0.3,
    label: { kind: "tag", text: "批量翻译 · 示例数据", at: [0.52, 0.085], from: 0.4, to: 2.9 },
  }),
  chunk("D5", 73.8, 77.2, {
    plate: "d-original", cam: cam(0.44, 0, 0.8), fadeIn: 0.3,
    cursor: [{ t: 1.2, at: [0.83, 0.27], click: true }],
    label: { kind: "tag", text: "仅更改游戏库内显示", at: [0.805, 0.052], from: 0.4, to: 3.0 },
  }),
  chunk("D6", 77.2, 80.0, {
    plate: "d-translated", cam: cam(0.44, 0, 0.8), fadeIn: 0.25,
    cursor: [{ t: 0.7, at: [0.83, 0.27], click: true }],
  }),
  chunk("D7", 80.0, 82.4, { plate: "d-translated", cam: cam(0.44, 0, 0.8) }),
  chunk("E1", 82.4, 86.0, { plate: "e-settings", cam: cam(0.44, 0, 0.62), fadeIn: 0.4 }),
  chunk("E2", 86.0, 89.6, { plate: "e-provider", cam: cam(0.44, 0, 0.62), fadeIn: 0.3 }),
  chunk("E3", 89.6, 92.4, {
    plate: "e-detail", cam: cam(0.44, 0.2, 0.8), fadeIn: 0.3,
    cursor: [{ t: 2.0, box: "配置或重试翻译插件", click: true }],
  }),
  chunk("E4", 92.4, 95.0, { plate: "e-installing", cam: cam(0.44, 0.2, 0.8) }),
  chunk("E5", 95.0, 99.0, {
    scene: "flowchart", fadeIn: 0.35,
    label: { kind: "chapter", text: "流程示意图", from: 0.2, to: 3.7 },
  }),
  chunk("E6", 99.0, 103.0, { plate: "e-configured", cam: cam(0.44, 0.2, 0.8), fadeIn: 0.4 }),
  chunk("F1", 103.0, 105.6, {
    plate: "f-mtool-detail", cam: cam(0.44, 0.26, 0.8), fadeIn: 0.3,
    cursor: [{ t: 1.5, box: "MTool 目录设置", click: true }],
  }),
  chunk("F2", 105.6, 108.2, {
    plate: "f-mtool-expanded", cam: cam(0.44, 0.1, 0.8), fadeIn: 0.25,
    cursor: [{ t: 0.9, box: "选择 MTool 目录", click: true }],
  }),
  chunk("F3", 108.2, 110.8, {
    plate: "f-mtool-ready", cam: cam(0.44, 0.1, 0.8), fadeIn: 0.25,
    cursor: [{ t: 0.6, box: "生成或修复 MTool 启动脚本", click: true }],
  }),
  chunk("F4", 110.8, 114.0, {
    plate: "f-mtool-ready", cam: cam(0.44, 0.12, 0.8), fadeIn: 0.2,
    label: { kind: "chapter", text: "外部工具 · 需自行准备 MTool", from: 0.2, to: 3.0 },
  }),
  chunk("G1", 114.0, 117.0, { plate: "g-library", cam: cam(0, 0, 1), fadeIn: 0.4 }),
  chunk("G2", 117.0, 119.6, { plate: "g-fav", cam: cam(0, 0, 0.99), fadeIn: 0.3 }),
  chunk("G3", 119.6, 122.6, { plate: "g-tag", cam: cam(0, 0, 0.99), fadeIn: 0.3 }),
  chunk("G4", 122.6, 125.2, { plate: "g-library", cam: cam(0, 0, 1), camTo: cam(0, 0, 0.97), fadeIn: 0.3 }),
  chunk("G5", 125.2, 129.2, {
    plate: "g-detail", cam: cam(0.44, 0, 0.8), fadeIn: 0.35,
    cursor: [{ t: 1.4, box: "开始游戏", click: true }],
  }),
  chunk("G6", 129.2, 132.0, {
    plate: "g-detail", cam: cam(0.44, 0, 0.8), camTo: cam(0.7, 0.3, 0.26), fadeIn: 0.25,
  }),
  chunk("H1", 132.0, 146.0, {
    plate: "k5-endcard", cam: cam(0, 0, 1), camTo: cam(0.015, 0.015, 0.97), fadeIn: 0.6,
  }),
];

fs.writeFileSync(
  path.join(ROOT, "config", "shots.json"),
  JSON.stringify({ duration: 146, chunks }, null, 2) + "\n",
);
console.log("chunks", chunks.length, "subtitles", subtitles.length);
