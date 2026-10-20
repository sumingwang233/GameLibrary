// Sharpness probe: does a scaled same-origin iframe stay crisp at DSF 2?
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const out = path.resolve(import.meta.dirname, "..", "out", "stills", "zoom-test.png");
const port = fs.readFileSync(path.resolve(import.meta.dirname, "..", "out", "host-port.txt"), "utf8").trim();
const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 810 }, deviceScaleFactor: 2 });
const page = await context.newPage();
await page.goto(`http://127.0.0.1:${port}/promo/film/stage-test.html`, { waitUntil: "networkidle" });
await page.waitForTimeout(1500);
await page.screenshot({ path: out });
await browser.close();
console.log(`[zoom-test] ${out}`);
