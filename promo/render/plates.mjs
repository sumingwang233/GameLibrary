/**
 * Capture product "plates": one screenshot per UI state used by the film.
 *
 * Each plate is the real product build (dist) served by ui-host/server.mjs with
 * the Tauri IPC shim pointed at the isolated demo library. Plates are captured at
 * 1440x810 CSS pixels with deviceScaleFactor 2, so every PNG is 2880x1620.
 *
 *   node render/plates.mjs            # all plates
 *   node render/plates.mjs d c        # only scenes whose id starts with d/c
 */
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright-core";

const ROOT = path.dirname(import.meta.dirname);
const THEME = process.argv.includes("--theme=light") ? "light" : "dark";
const OUT = path.join(ROOT, "out", "plates", THEME);
fs.mkdirSync(OUT, { recursive: true });
const PORT = fs.readFileSync(path.join(ROOT, "out", "host-port.txt"), "utf8").trim();
const BASE = `http://127.0.0.1:${PORT}/`;

const results = [];

async function openScene(browser, scene) {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 810 },
    deviceScaleFactor: 2,
    locale: "zh-CN",
    timezoneId: "Asia/Shanghai",
    colorScheme: THEME,
  });
  const page = await context.newPage();
  page.on("pageerror", (error) => console.error("[pageerror]", scene, error.message));
  await page.goto(`${BASE}?scene=${scene}&theme=${THEME}`, { waitUntil: "networkidle" });
  await page.waitForSelector("main", { timeout: 15000 });
  await page.waitForTimeout(500);
  return { context, page };
}

async function snap(page, name) {
  const file = path.join(OUT, name + ".png");
  const boxes = await page.evaluate(() => {
    const out = [];
    const nodes = document.querySelectorAll("button, [role=button], [role=checkbox], input, textarea, [role=tab], summary");
    for (const el of nodes) {
      const label = (el.getAttribute("aria-label") || el.textContent || "").replace(/\s+/g, " ").trim().slice(0, 40);
      const rect = el.getBoundingClientRect();
      if (!label || rect.width < 4 || rect.height < 4) continue;
      out.push({
        label,
        x: +(rect.x / innerWidth).toFixed(4),
        y: +(rect.y / innerHeight).toFixed(4),
        w: +(rect.width / innerWidth).toFixed(4),
        h: +(rect.height / innerHeight).toFixed(4),
      });
    }
    return out;
  });
  await page.screenshot({ path: file });
  const size = fs.statSync(file).size;
  results.push({ name, bytes: size, boxes });
  console.log("[plate]", name, (size / 1024).toFixed(0) + " KB", boxes.length + " boxes");
}

const btn = (page, name) => page.getByRole("button", { name, exact: true }).first();
const tab = (page, name) => page.getByRole("tab", { name, exact: true }).first();
const card = (page, title) => page.getByRole("button", { name: `${title}，查看详情`, exact: true });
const check = (page, title) => page.getByRole("checkbox", { name: `选择 ${title}`, exact: true });

// ------------------------------------------------------------------ B
async function sceneB(browser) {
  const { context, page } = await openScene(browser, "scan");
  await btn(page, "游戏库目录").click();
  await page.waitForTimeout(400);
  await snap(page, "b-roots-before");

  // Adding a root goes through the native folder picker; the shim answers it, so the
  // product's real addRoot path runs and the row appears without any fake dialog art.
  await page.getByRole("button", { name: "添加游戏库" }).last().click();
  await page.waitForTimeout(900);
  await snap(page, "b-roots-after");

  await btn(page, "扫描游戏库").click();
  await page.waitForTimeout(1200);
  await snap(page, "b-scanning");

  await page.waitForTimeout(2600);
  await page.getByRole("button", { name: /待确认/ }).first().click();
  await page.waitForTimeout(600);
  await snap(page, "b-review");

  await page.getByRole("button", { name: "全选已加载项目" }).click();
  await page.waitForTimeout(300);
  await snap(page, "b-review-selected");

  await btn(page, "加入").click();
  await page.waitForTimeout(1400);
  await page.getByRole("button", { name: /^全部游戏/ }).first().click();
  await page.waitForTimeout(700);
  await snap(page, "b-library-after");

  await card(page, "Hollow Tide").click();
  await page.waitForTimeout(600);
  await tab(page, "启动").click();
  await page.waitForTimeout(500);
  await snap(page, "b-detail-launch");
  await page.evaluate(() => {
    const scrollers = [...document.querySelectorAll("[role=dialog] *")].filter(
      (node) => node.scrollHeight > node.clientHeight + 40 && ["auto", "scroll"].includes(getComputedStyle(node).overflowY),
    );
    for (const node of scrollers) node.scrollTop = node.scrollHeight;
  });
  await page.waitForTimeout(400);
  await snap(page, "b-detail-launch-bottom");
  await context.close();
}

// ------------------------------------------------------------------ C
async function sceneC(browser) {
  const { context, page } = await openScene(browser, "library");
  await snap(page, "c-library");

  const search = page.getByPlaceholder("搜索游戏标题或路径");
  await search.fill("harbor");
  await page.waitForTimeout(600);
  await snap(page, "c-search");
  await search.fill("");
  await page.waitForTimeout(400);

  await check(page, "Glass Harbor").check();
  await page.waitForTimeout(300);
  await btn(page, "批量收藏").click();
  await page.waitForTimeout(1400);
  await snap(page, "c-fav");

  await page.getByRole("button", { name: /^收藏/ }).first().click();
  await page.waitForTimeout(700);
  await snap(page, "c-favlist");

  await page.getByRole("button", { name: /^想重玩/ }).first().click();
  await page.waitForTimeout(700);
  await snap(page, "c-tag");

  await page.getByRole("button", { name: /^全部游戏/ }).first().click();
  await page.waitForTimeout(500);
  await card(page, "Hollow Tide").click();
  await page.waitForTimeout(700);
  await snap(page, "c-detail");
  await context.close();
}

// ------------------------------------------------------------------ D
// Scroll the open detail sheet so `text` sits ~130px below the sheet top.
// Needed because captions live in the bottom band of the film frame.
async function scrollSheetTo(page, text) {
  await page.evaluate((needle) => {
    const dialogs = [...document.querySelectorAll("[role=dialog]")];
    const scope = dialogs[dialogs.length - 1] || document;
    const scrollers = [...scope.querySelectorAll("*")].filter(
      (node) => node.scrollHeight > node.clientHeight + 40 && ["auto", "scroll"].includes(getComputedStyle(node).overflowY),
    );
    const target = [...scope.querySelectorAll("button, summary, h3, h4, span, div")].find((node) =>
      (node.textContent || "").trim().startsWith(needle),
    );
    if (!scrollers[0] || !target) return;
    const scroller = scrollers[0];
    const box = target.getBoundingClientRect();
    const frame = scroller.getBoundingClientRect();
    scroller.scrollTop += box.top - frame.top - 130;
  }, text);
  await page.waitForTimeout(260);
}

// ------------------------------------------------------------------ D
async function sceneD(browser) {
  const { context, page } = await openScene(browser, "translate");
  await card(page, "Ashen Vale").click();
  await page.waitForTimeout(700);
  await scrollSheetTo(page, "翻译为简体中文");
  await snap(page, "d-detail");

  await btn(page, "翻译为简体中文").click();
  await page.waitForTimeout(600);
  await snap(page, "d-processing");

  await page.waitForTimeout(2400);
  await scrollSheetTo(page, "重新翻译名称");
  await snap(page, "d-translated");

  await btn(page, "显示原文").click();
  await page.waitForTimeout(500);
  await scrollSheetTo(page, "显示中文译名");
  await snap(page, "d-original");

  await btn(page, "显示中文译名").click();
  await page.waitForTimeout(300);
  await page.keyboard.press("Escape");
  await page.waitForTimeout(600);

  await check(page, "Glass Harbor").check();
  await check(page, "Neon Drift").check();
  await page.waitForTimeout(200);
  await btn(page, "翻译选中名称").click();
  await page.waitForTimeout(800);
  await snap(page, "d-batch");
  await context.close();
}

// ------------------------------------------------------------------ E
async function sceneE(browser) {
  const { context, page } = await openScene(browser, "unity");
  await card(page, "Hollow Tide").click();
  await page.waitForTimeout(600);
  await tab(page, "启动").click();
  await page.waitForTimeout(500);
  await scrollSheetTo(page, "Unity 游戏内翻译");
  await snap(page, "e-detail");

  await page.getByRole("button", { name: /配置或重试翻译插件/ }).click();
  await page.waitForTimeout(800);
  await scrollSheetTo(page, "Unity 游戏内翻译");
  await snap(page, "e-installing");

  // The card polls unity_translation.status every 2.5 s; the mock reaches "configured"
  // at t+3 s, so wait through two polls before capturing the settled state.
  await page.waitForTimeout(6000);
  await scrollSheetTo(page, "Unity 游戏内翻译");
  await snap(page, "e-configured");

  await page.keyboard.press("Escape");
  await page.waitForTimeout(500);
  await btn(page, "设置").click();
  await page.waitForTimeout(600);
  await tab(page, "游戏库").click();
  await page.waitForTimeout(500);
  await snap(page, "e-settings");

  await page.getByRole("button", { name: "配置翻译供应商与密钥" }).click();
  await page.waitForTimeout(800);
  await snap(page, "e-provider");
  await context.close();
}

// ------------------------------------------------------------------ F
async function sceneF(browser) {
  const { context, page } = await openScene(browser, "mtool");
  await card(page, "Iron Meridian").click();
  await page.waitForTimeout(600);
  await tab(page, "启动").click();
  await page.waitForTimeout(500);
  await scrollSheetTo(page, "MTool 翻译");
  await snap(page, "f-mtool-detail");

  await page.getByText("MTool 目录设置", { exact: true }).click();
  await page.waitForTimeout(400);
  await scrollSheetTo(page, "MTool 目录设置");
  await snap(page, "f-mtool-expanded");

  await page.getByRole("button", { name: "选择 MTool 目录" }).click();
  await page.waitForTimeout(900);
  await scrollSheetTo(page, "MTool 翻译");
  await snap(page, "f-mtool-settings");

  const prepare = page.getByRole("button", { name: /生成启动脚本/ });
  await prepare.first().click();
  await page.waitForTimeout(1400);
  await scrollSheetTo(page, "MTool 翻译");
  await snap(page, "f-mtool-ready");
  await context.close();
}

// ------------------------------------------------------------------ G
async function sceneG(browser) {
  const { context, page } = await openScene(browser, "final");
  await snap(page, "g-library");
  await page.getByRole("button", { name: /^收藏/ }).first().click();
  await page.waitForTimeout(700);
  await snap(page, "g-fav");
  await page.getByRole("button", { name: /^想重玩/ }).first().click();
  await page.waitForTimeout(700);
  await snap(page, "g-tag");
  await page.getByRole("button", { name: /^全部游戏/ }).first().click();
  await page.waitForTimeout(400);
  await card(page, "Skyward Ledger").click();
  await page.waitForTimeout(700);
  await snap(page, "g-detail");
  await context.close();
}

const scenes = { b: sceneB, c: sceneC, d: sceneD, e: sceneE, f: sceneF, g: sceneG };

const pick = process.argv.slice(2).filter((arg) => !arg.startsWith("--"));
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  for (const [id, run] of Object.entries(scenes)) {
    if (pick.length && !pick.includes(id)) continue;
    console.log("== scene", id);
    await run(browser);
  }
} finally {
  await browser.close();
}
const manifestPath = path.join(OUT, "plates.json");
const previous = fs.existsSync(manifestPath) ? JSON.parse(fs.readFileSync(manifestPath, "utf8")) : [];
const merged = new Map(previous.map((entry) => [entry.name, entry]));
for (const entry of results) merged.set(entry.name, entry);
fs.writeFileSync(manifestPath, JSON.stringify([...merged.values()], null, 2));
console.log("plates", merged.size);



