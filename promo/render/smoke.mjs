import { chromium } from 'playwright-core';

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const ctx = await browser.newContext({
  viewport: { width: 1440, height: 810 },
  deviceScaleFactor: 2,
});
const page = await ctx.newPage();
await page.setContent(`<!doctype html><html><body style="margin:0;height:100vh;background:#171a21;color:#c7d5e0;
font-family:'Microsoft YaHei UI','Noto Sans SC',sans-serif;display:flex;flex-direction:column;justify-content:center;gap:24px;padding:80px;box-sizing:border-box">
<div style="font-size:72px;font-weight:700;letter-spacing:.02em">把散落的游戏整理到一处</div>
<div style="font-size:48px;color:#66c0f4">GameLibrary · v1.7.22 · 开发版演示</div>
<div style="font-size:40px;color:#8f98a0">Unity 翻译插件 · MTool 接入 · 名称翻译 0.5s 12:34 标签</div>
</body></html>`);
const t0 = Date.now();
await page.screenshot({ path: 'out/stills/smoke.png' });
console.log('screenshot ms', Date.now() - t0);
const size = await page.evaluate(() => ({ w: innerWidth, h: innerHeight, dpr: devicePixelRatio }));
console.log(JSON.stringify(size));
await browser.close();
