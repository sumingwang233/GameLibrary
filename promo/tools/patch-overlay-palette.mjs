import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..");
const file = path.join(ROOT, "film", "film.js");
let s = fs.readFileSync(file, "utf8");
let changed = 0;

const swap = (from, to) => {
  if (!s.includes(from)) {
    console.error("missing snippet:", from.slice(0, 60));
    process.exit(1);
  }
  s = s.replace(from, to);
  changed += 1;
};

swap(
  'const P = THEME === "light" ? LIGHT : DARK;',
  'const P = THEME === "light" ? LIGHT : DARK;\nlet OVERLAY = P;',
);
swap(
  "  scrim.addColorStop(0, P.caption[0]);\n  scrim.addColorStop(0.32, P.caption[1]);\n  scrim.addColorStop(1, P.caption[2]);",
  "  scrim.addColorStop(0, OVERLAY.caption[0]);\n  scrim.addColorStop(0.32, OVERLAY.caption[1]);\n  scrim.addColorStop(1, OVERLAY.caption[2]);",
);
swap(
  "    ctx.fillStyle = P.captionShadow;\n    ctx.fillText(line, W / 2 + 2, y + 2);\n    ctx.fillStyle = P.captionText;",
  "    ctx.fillStyle = OVERLAY.captionShadow;\n    ctx.fillText(line, W / 2 + 2, y + 2);\n    ctx.fillStyle = OVERLAY.captionText;",
);
swap(
  'function drawVignette() {\n  if (THEME === "light") {',
  'function drawVignette() {\n  if (OVERLAY === LIGHT) {',
);
swap(
  "    gradient.addColorStop(1, P.vignette);\n    ctx.fillStyle = gradient;\n    ctx.fillRect(0, 0, W, H);\n    return;",
  "    gradient.addColorStop(1, OVERLAY.vignette);\n    ctx.fillStyle = gradient;\n    ctx.fillRect(0, 0, W, H);\n    return;",
);
swap(
  "  const showOther = t >= 1.05 && t < 2.45;",
  '  const showOther = t >= 1.05 && t < 2.45;\n  OVERLAY = showOther ? (OTHER_THEME === "light" ? LIGHT : DARK) : P;',
);
swap(
  "function drawChunk(chunk, t) {",
  'function drawChunk(chunk, t) {\n  OVERLAY = P;',
);

fs.writeFileSync(file, s);
console.log("patched", changed, "snippets");
