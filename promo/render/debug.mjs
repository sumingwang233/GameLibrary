import fs from "node:fs";
import { chromium } from "playwright-core";
const PORT = fs.readFileSync("out/host-port.txt","utf8").trim();
const browser = await chromium.launch({ channel: "chrome", headless: true });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
page.on("pageerror", e => console.error("pageerror", e.message));
await page.goto(`http://127.0.0.1:${PORT}/promo/film/index.html`, { waitUntil: "networkidle" });
await page.waitForFunction(() => window.__FILM_READY__ === true);
const out = await page.evaluate(() => {
  const c = document.getElementById("stage");
  const g = c.getContext("2d");
  window.renderAt(3);
  const px = [...g.getImageData(400, 500, 1, 1).data];
  const px2 = [...g.getImageData(300, 300, 1, 1).data];
  return { last: window.__lastDrawn, px, px2, alpha: g.globalAlpha, transform: g.getTransform().a };
});
console.log(JSON.stringify(out));
await browser.close();
