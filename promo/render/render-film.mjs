// Renders the film deterministically, frame by frame, and pipes the frames straight
// into the project-local ffmpeg so no intermediate PNG sequence touches the disk.
//
//   node render/render-film.mjs --out=out/film/subtitled-silent.mp4 --captions=1
//   node render/render-film.mjs --out=out/film/clean-silent.mp4 --captions=0
//   node render/render-film.mjs --from=0 --to=6 --out=out/film/test.mp4
import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { chromium } from "playwright-core";

const ROOT = path.resolve(import.meta.dirname, "..");
const FFMPEG = path.join(ROOT, "tools", "ffmpeg", "ffmpeg.exe");
const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [k, v = "1"] = a.replace(/^--/, "").split("=");
    return [k, v];
  }),
);
const out = path.resolve(ROOT, args.out || "out/film/film-silent.mp4");
const captions = args.captions !== "0" ? 1 : 0;
const from = Number(args.from || 0);
const to = Number(args.to || 0);

const portFile = path.join(ROOT, "out", "host-port.txt");
const port = fs.existsSync(portFile) ? fs.readFileSync(portFile, "utf8").trim() : "18471";
const url = `http://127.0.0.1:${port}/promo/film/index.html?captions=${captions}`;

fs.mkdirSync(path.dirname(out), { recursive: true });

const browser = await chromium.launch({ channel: "chrome", headless: true });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
page.on("pageerror", (error) => console.error("[pageerror]", error.message));
await page.goto(url, { waitUntil: "networkidle" });
await page.waitForFunction("window.__FILM_READY__ === true", null, { timeout: 120000 });
const info = await page.evaluate("window.__FILM_INFO__");
const fps = info.fps;
const end = to > 0 ? Math.min(to, info.duration) : info.duration;
const total = Math.max(1, Math.round((end - from) * fps));
console.log(`[film] ${info.duration}s @ ${fps}fps  captions=${captions}  frames=${total}`);

const ffmpeg = spawn(
  FFMPEG,
  [
    "-y", "-hide_banner", "-loglevel", "error",
    "-f", "image2pipe", "-framerate", String(fps), "-i", "-",
    "-c:v", "libx264", "-preset", "slow", "-crf", "16", "-pix_fmt", "yuv420p",
    "-r", String(fps), "-movflags", "+faststart", out,
  ],
  { stdio: ["pipe", "inherit", "inherit"] },
);
const done = new Promise((resolve, reject) => {
  ffmpeg.on("error", reject);
  ffmpeg.on("close", (code) => (code === 0 ? resolve() : reject(new Error("ffmpeg exit " + code))));
});

const started = Date.now();
const client = await page.context().newCDPSession(page);
for (let i = 0; i < total; i += 1) {
  const t = from + i / fps;
  await page.evaluate((time) => window.renderAt(time), t);
  const shot = await client.send("Page.captureScreenshot", { format: "jpeg", quality: 94, fromSurface: true });
  const buffer = Buffer.from(shot.data, "base64");
  if (!ffmpeg.stdin.write(buffer)) {
    await new Promise((resolve) => ffmpeg.stdin.once("drain", resolve));
  }
  if (i % 300 === 0 || i === total - 1) {
    const elapsed = (Date.now() - started) / 1000;
    const rate = (i + 1) / elapsed;
    console.log(
      `[film] ${i + 1}/${total}  t=${t.toFixed(2)}  ${rate.toFixed(1)} fps  eta ${Math.round((total - i - 1) / rate)}s`,
    );
  }
}
ffmpeg.stdin.end();
await done;
await browser.close();
const size = fs.statSync(out).size;
console.log(`[film] wrote ${path.relative(ROOT, out)} (${(size / 1048576).toFixed(1)} MB) in ${Math.round((Date.now() - started) / 1000)}s`);
