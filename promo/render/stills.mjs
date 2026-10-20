// Key stills for the first review checkpoint (real frontend + isolated mock data).
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const PORT = fs.readFileSync(path.resolve(import.meta.dirname, "..", "out", "host-port.txt"), "utf8").trim();
const BASE = `http://127.0.0.1:${PORT}`;
const OUT = path.resolve(import.meta.dirname, "..", "out", "stills");

const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 810 }, deviceScaleFactor: 2 });

async function open(scene) {
  const page = await context.newPage();
  await page.goto(`${BASE}/?scene=${scene}`, { waitUntil: "networkidle" });
  await page.waitForSelector("article[role=button]", { timeout: 30000 });
  await page.waitForTimeout(700);
  return page;
}

async function shoot(page, name) {
  const file = path.join(OUT, name);
  await page.screenshot({ path: file });
  console.log("[still]", name);
}

async function openGame(page, title) {
  const card = page.locator(`article[role=button][aria-label^="${title}"]`).first();
  await card.click();
  await page.waitForTimeout(650);
}

// 1) library overview
{
  const page = await open("library");
  await shoot(page, "k1-library.png");
  await page.close();
}

// 2) title translation: translated display + original display
{
  const page = await open("translate");
  await openGame(page, "Ashen Vale");
  const translate = page.getByRole("button", { name: /翻译为简体中文|重新翻译名称/ }).first();
  if (await translate.count()) {
    await translate.click();
    await page.waitForTimeout(3600);
  }
  await shoot(page, "k2-translated.png");
  await page.getByRole("button", { name: "显示原文", exact: true }).click();
  await page.waitForTimeout(500);
  await shoot(page, "k2-original.png");
  await page.close();
}

// 3) Unity plugin configured, waiting for an in-game check
{
  const page = await open("unity");
  await openGame(page, "Hollow Tide");
  await page.getByRole("tab", { name: "启动" }).click();
  await page.waitForTimeout(500);
  const configure = page.getByRole("button", { name: /配置或重试翻译插件/ }).first();
  if (await configure.count()) {
    await configure.click();
    await page.waitForTimeout(6800);
  }
  await shoot(page, "k3-unity.png");
  await page.close();
}

// 4) MTool launch script prepared
{
  const page = await open("final");
  await openGame(page, "Iron Meridian");
  await page.getByRole("tab", { name: "启动" }).click();
  await page.waitForTimeout(500);
  const generate = page.getByRole("button", { name: /生成或修复 MTool 启动脚本|标记为需要翻译并生成启动脚本/ }).first();
  if (await generate.count()) {
    await generate.click();
    await page.waitForTimeout(900);
  }
  await shoot(page, "k4-mtool.png");
  await page.close();
}

// 5) brand end card
{
  const page = await context.newPage();
  await page.goto(`${BASE}/promo/film/endcard.html`, { waitUntil: "networkidle" });
  await page.waitForTimeout(600);
  await shoot(page, "k5-endcard.png");
  await page.close();
}

await browser.close();

