/* promo/render/probe.mjs — open the mocked app and grab one still.
 * usage: node render/probe.mjs <scene> <outfile> [waitMs] [viewport]
 */
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const [scene = "library", outfile = "out/stills/probe.png", waitMs = "900", viewportArg = "1440x810"] = process.argv.slice(2);
const [width, height] = viewportArg.split("x").map(Number);
const portFile = path.resolve(import.meta.dirname, "..", "out", "host-port.txt");
const port = fs.existsSync(portFile) ? fs.readFileSync(portFile, "utf8").trim() : "18471";

const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 2, locale: "zh-CN", timezoneId: "Asia/Singapore" });
const page = await context.newPage();
page.on("console", (message) => { if (message.type() === "error" || message.type() === "warning") console.log("[app]", message.type(), message.text()); });
page.on("pageerror", (error) => console.log("[app:error]", error.message));

await page.goto(`http://127.0.0.1:${port}/?scene=${scene}`, { waitUntil: "networkidle" });
try {
  await page.waitForSelector('[aria-label*="查看详情"]', { timeout: 12000 });
} catch {
  console.log("[probe] no game card appeared; dumping body text");
  console.log((await page.locator("body").innerText()).slice(0, 1200));
}
await page.waitForTimeout(Number(waitMs));
fs.mkdirSync(path.dirname(outfile), { recursive: true });
await page.screenshot({ path: outfile, animations: "disabled" });
console.log("[probe] " + outfile);
await browser.close();

