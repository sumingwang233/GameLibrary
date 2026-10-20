/**
 * Render single frames of the film stage for review.
 *   node render/stillframe.mjs 3 14 25.5 ...
 */
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright-core";

const ROOT = path.dirname(import.meta.dirname);
const PORT = fs.readFileSync(path.join(ROOT, "out", "host-port.txt"), "utf8").trim();
const OUT = path.join(ROOT, "out", "frames-test");
fs.mkdirSync(OUT, { recursive: true });

const times = process.argv.slice(2).map(Number).filter((value) => Number.isFinite(value));
if (!times.length) times.push(0);

const browser = await chromium.launch({ channel: "chrome", headless: true });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
page.on("console", (message) => {
  if (message.type() === "error") console.error("[console]", message.text());
});
page.on("pageerror", (error) => console.error("[pageerror]", error.message));
await page.goto(`http://127.0.0.1:${PORT}/promo/film/index.html`, { waitUntil: "networkidle" });
await page.waitForFunction(() => window.__FILM_READY__ === true, null, { timeout: 30000 });

for (const t of times) {
  const started = Date.now();
  await page.evaluate((time) => window.renderAt(time), t);
  const file = path.join(OUT, "t" + t.toFixed(2).replace(".", "_") + ".png");
  await page.screenshot({ path: file });
  console.log("[frame]", t, (Date.now() - started) + " ms", file);
}
await browser.close();
