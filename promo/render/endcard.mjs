/**
 * Capture the end card in both themes into the plate folders.
 *   node render/endcard.mjs
 */
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright-core";

const ROOT = path.dirname(import.meta.dirname);
const PORT = fs.readFileSync(path.join(ROOT, "out", "host-port.txt"), "utf8").trim();
const BASE = `http://127.0.0.1:${PORT}/promo/film/endcard.html`;

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  for (const theme of ["dark", "light"]) {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 810 },
      deviceScaleFactor: 2,
      locale: "zh-CN",
      colorScheme: theme,
    });
    const page = await context.newPage();
    await page.goto(`${BASE}?theme=${theme}`, { waitUntil: "networkidle" });
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(150);
    const out = path.join(ROOT, "out", "plates", theme, "k5-endcard.png");
    fs.mkdirSync(path.dirname(out), { recursive: true });
    await page.screenshot({ path: out });
    console.log("[endcard]", theme, (fs.statSync(out).size / 1024).toFixed(0) + " KB");
    await context.close();
  }
} finally {
  await browser.close();
}
