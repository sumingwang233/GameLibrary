// Probe: CDP screencast frame size + rate at DSF 2, and whether frames can exceed CSS pixels.
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const port = fs.readFileSync(path.resolve(import.meta.dirname, "..", "out", "host-port.txt"), "utf8").trim();
const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 810 }, deviceScaleFactor: 2 });
const page = await context.newPage();
await page.goto(`http://127.0.0.1:${port}/?scene=library`, { waitUntil: "networkidle" });
await page.waitForTimeout(1200);

const cdp = await context.newCDPSession(page);
const frames = [];
cdp.on("Page.screencastFrame", async (frame) => {
  frames.push({
    at: Date.now(),
    px: Buffer.from(frame.data, "base64").subarray(0, 0).length,
    size: frame.data.length,
    meta: frame.metadata,
    sessionId: frame.sessionId,
  });
  if (frame.sessionId !== undefined) await cdp.send("Page.screencastFrameAck", { sessionId: frame.sessionId }).catch(() => {});
});
const started = Date.now();
await cdp.send("Page.startScreencast", { format: "png", maxWidth: 2880, maxHeight: 1620, everyNthFrame: 1 });

await page.locator("article[role=button]").first().click();
await page.waitForTimeout(1600);
await cdp.send("Page.stopScreencast");

// Decode one frame to learn its pixel size.
const first = frames.find((f) => f.size > 1000);
let decodedSize = null;
if (first) {
  const fsmod = await import("node:fs");
  const shot = await page.screenshot();
  decodedSize = shot.length;
}
console.log(JSON.stringify({
  count: frames.length,
  durationMs: Date.now() - started,
  fps: +(frames.length / ((Date.now() - started) / 1000)).toFixed(1),
  sizes: frames.slice(0, 3).map((f) => f.size),
  meta: frames.slice(0, 3).map((f) => f.meta),
  shotBytes: decodedSize,
}, null, 2));
await browser.close();
