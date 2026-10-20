/**
 * Film stage. Everything visible is a pure function of `t` (seconds):
 *   window.renderAt(t)
 * No timers, no accumulated state, no randomness without a fixed seed.
 *
 * Canvas is 1920x1080 CSS pixels backed by 3840x2160 device pixels, so the
 * frame that Playwright screenshots at 1920x1080 is a clean downsample.
 *
 * Theme: ?theme=light renders the light build (light plates + light palette).
 */
const W = 1920;
const H = 1080;
const DSF = 2;

const canvas = document.getElementById("stage");
const ctx = canvas.getContext("2d", { alpha: false });
canvas.width = W * DSF;
canvas.height = H * DSF;
canvas.style.width = W + "px";
canvas.style.height = H + "px";

const FONT = "'Microsoft YaHei UI', 'Microsoft YaHei', 'Noto Sans SC', sans-serif";
const PARAMS = new URLSearchParams(location.search);
const THEME = PARAMS.get("theme") === "light" ? "light" : "dark";
const OTHER_THEME = THEME === "light" ? "dark" : "light";

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const lerp = (a, b, p) => a + (b - a) * p;
const easeInOut = (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2);
const easeOut = (p) => 1 - Math.pow(1 - p, 3);
const smooth = (p) => p * p * (3 - 2 * p);
const fadeWindow = (t, a, b) => clamp((t - a) / Math.max(0.001, b - a), 0, 1);

// ------------------------------------------------------------------ palette
const DARK = {
  bg: ["#181c24", "#14171f", "#0f1218"],
  grid: "rgba(120,160,200,0.045)",
  vignette: "rgba(0,0,0,0.30)",
  card: "#1b2838",
  cardTab: "#243447",
  cardUnknown: "#202630",
  cardStroke: "rgba(48,71,94,0.95)",
  cardGlowStroke: "rgba(102,192,244,0.85)",
  cardGlow: "rgba(102,192,244,0.55)",
  ink: "#e6eef5",
  inkSoft: "#c7d5e0",
  inkFaint: "#8f98a0",
  accent: "#66c0f4",
  accentBright: "#8ed5f7",
  accentSoft: "rgba(102,192,244,0.12)",
  accentStroke: "rgba(102,192,244,0.55)",
  panel: "#1b2838",
  panelStroke: "#30475e",
  panelShadow: "rgba(0,0,0,0.45)",
  field: "#121821",
  fieldStroke: "#30475e",
  row: "#1e2a38",
  rowActive: "rgba(102,192,244,0.12)",
  rowActiveStroke: "rgba(102,192,244,0.7)",
  rowIcon: "#2a475e",
  node: "#1b2838",
  nodeActive: "#22354a",
  nodeStroke: "rgba(48,71,94,0.9)",
  good: "#a4d007",
  goodSoft: "rgba(164,208,7,0.16)",
  goodStroke: "rgba(164,208,7,0.85)",
  brandText: "#ffffff",
  brandSub: "#8f98a0",
  brandGlow: "rgba(11,14,19,1)",
  chipBg: "rgba(102,192,244,0.12)",
  chipStroke: "rgba(102,192,244,0.45)",
  chipText: "#8ed5f7",
  caption: ["rgba(6,9,13,0)", "rgba(6,9,13,0.62)", "rgba(6,9,13,0.9)"],
  captionText: "#f2f6fa",
  captionShadow: "rgba(0,0,0,0.55)",
  labelBg: "rgba(9,13,19,0.95)",
  labelText: "#dfe9f2",
  cursorFill: "#ffffff",
  cursorStroke: "rgba(9,12,17,0.85)",
};

const LIGHT = {
  bg: ["#f9fbfd", "#eef3f8", "#e4ebf3"],
  grid: "rgba(60,90,120,0.065)",
  vignette: "rgba(40,70,100,0.10)",
  card: "#ffffff",
  cardTab: "#e2eaf3",
  cardUnknown: "#eef2f7",
  cardStroke: "rgba(150,170,190,0.55)",
  cardGlowStroke: "rgba(26,115,184,0.75)",
  cardGlow: "rgba(26,115,184,0.28)",
  ink: "#1b2838",
  inkSoft: "#3d4c5c",
  inkFaint: "#64748a",
  accent: "#1a73b8",
  accentBright: "#155e96",
  accentSoft: "rgba(26,115,184,0.10)",
  accentStroke: "rgba(26,115,184,0.45)",
  panel: "#ffffff",
  panelStroke: "#cbd8e4",
  panelShadow: "rgba(40,70,100,0.18)",
  field: "#f3f7fb",
  fieldStroke: "#cbd8e4",
  row: "#f7fafc",
  rowActive: "rgba(26,115,184,0.10)",
  rowActiveStroke: "rgba(26,115,184,0.55)",
  rowIcon: "#d7e6f4",
  node: "#ffffff",
  nodeActive: "#eaf3fb",
  nodeStroke: "rgba(180,198,215,0.9)",
  good: "#4c8c0a",
  goodSoft: "rgba(76,140,10,0.12)",
  goodStroke: "rgba(76,140,10,0.7)",
  brandText: "#16202b",
  brandSub: "#5a6b7d",
  brandGlow: "rgba(249,251,253,1)",
  chipBg: "rgba(26,115,184,0.10)",
  chipStroke: "rgba(26,115,184,0.40)",
  chipText: "#155e96",
  caption: ["rgba(247,250,253,0)", "rgba(247,250,253,0.55)", "rgba(247,250,253,0.80)"],
  captionText: "#1b2838",
  captionShadow: "rgba(255,255,255,0.85)",
  labelBg: "rgba(255,255,255,0.96)",
  labelText: "#16202b",
  cursorFill: "#1b2838",
  cursorStroke: "rgba(255,255,255,0.9)",
};

const P = THEME === "light" ? LIGHT : DARK;
let OVERLAY = P;

async function loadJSON(url) {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) throw new Error(url + " -> " + response.status);
  return response.json();
}

function loadImage(src) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error("image failed: " + src));
    image.src = src;
  });
}

const [FILM, COPY, SHOTS, PLATE_MANIFEST] = await Promise.all([
  loadJSON("../config/film.json"),
  loadJSON("../config/copy.json"),
  loadJSON("../config/shots.json"),
  loadJSON("../out/plates/" + THEME + "/plates.json"),
]);

const SUBTITLES = (COPY.subtitles || []).map((item) => ({
  ...item,
  text: String(item.text || "").replace(/[。]\s*$/, ""),
}));
const CHUNKS = SHOTS.chunks;
const BOXES = new Map(PLATE_MANIFEST.map((entry) => [entry.name, entry.boxes || []]));

const plateNames = [...new Set(CHUNKS.map((chunk) => chunk.plate).filter(Boolean))];
const PLATES = new Map();
const CROSS_PLATES = new Map();
await Promise.all([
  ...plateNames.map(async (name) => {
    PLATES.set(name, await loadImage("../out/plates/" + THEME + "/" + name + ".png"));
  }),
  ...plateNames.map(async (name) => {
    CROSS_PLATES.set(name, await loadImage("../out/plates/" + OTHER_THEME + "/" + name + ".png"));
  }),
  loadImage("assets/app-icon.png").then((image) => PLATES.set("__icon", image)),
]);

function boxOf(plateName, label) {
  const list = BOXES.get(plateName) || [];
  const exact = list.find((box) => box.label === label);
  if (exact) return exact;
  const loose = list.find((box) => box.label.indexOf(label) === 0);
  return loose || null;
}

// ------------------------------------------------------------------ geometry
function cameraOf(chunk, t) {
  const [x, y, size] = chunk.cam || [0, 0, 1];
  if (!chunk.camTo) return { x, y, size };
  const span = Math.max(0.001, chunk.t1 - chunk.t0);
  const p = clamp((t - chunk.t0) / span, 0, 1);
  const e = easeInOut(p);
  const [tx, ty, tsize] = chunk.camTo;
  return { x: lerp(x, tx, e), y: lerp(y, ty, e), size: lerp(size, tsize, e) };
}

function roundRect(x, y, w, h, r) {
  const radius = Math.min(r, w / 2, h / 2);
  ctx.beginPath();
  ctx.moveTo(x + radius, y);
  ctx.arcTo(x + w, y, x + w, y + h, radius);
  ctx.arcTo(x + w, y + h, x, y + h, radius);
  ctx.arcTo(x, y + h, x, y, radius);
  ctx.arcTo(x, y, x + w, y, radius);
  ctx.closePath();
}

function fillRound(x, y, w, h, r, fill) {
  roundRect(x, y, w, h, r);
  ctx.fillStyle = fill;
  ctx.fill();
}

function strokeRound(x, y, w, h, r, stroke, width = 1) {
  roundRect(x, y, w, h, r);
  ctx.strokeStyle = stroke;
  ctx.lineWidth = width;
  ctx.stroke();
}

function drawText(text, x, y, size, color, weight = 400, align = "left") {
  ctx.font = weight + " " + size + "px " + FONT;
  ctx.fillStyle = color;
  ctx.textAlign = align;
  ctx.textBaseline = "alphabetic";
  ctx.fillText(text, x, y);
}

// ------------------------------------------------------------------ plate
function drawPlateImage(image, chunk, t) {
  if (!image) return;
  const cam = cameraOf(chunk, t);
  const sw = cam.size * image.width;
  const sh = cam.size * image.height;
  const sx = clamp(cam.x, 0, 1 - cam.size) * image.width;
  const sy = clamp(cam.y, 0, 1 - cam.size) * image.height;
  ctx.drawImage(image, sx, sy, sw, sh, 0, 0, W, H);
}

function drawPlate(chunk, t) {
  drawPlateImage(PLATES.get(chunk.plate), chunk, t);
}

function plateToScreen(cam, nx, ny) {
  const cx = clamp(cam.x, 0, 1 - cam.size);
  const cy = clamp(cam.y, 0, 1 - cam.size);
  return { x: ((nx - cx) / cam.size) * W, y: ((ny - cy) / cam.size) * H };
}

// ------------------------------------------------------------------ cursor
function cursorEntries(chunk) {
  const entries = [];
  for (const entry of chunk.cursor || []) {
    let point = null;
    if (entry.at) point = entry.at;
    else if (entry.box) {
      const box = boxOf(chunk.plate, entry.box);
      if (box) point = [box.x + box.w / 2, box.y + box.h / 2];
    }
    if (point) entries.push({ t: entry.t, at: point, click: entry.click });
  }
  return entries.sort((a, b) => a.t - b.t);
}

function drawCursor(chunk, t) {
  const entries = cursorEntries(chunk);
  if (!entries.length) return;
  const local = t - chunk.t0;
  const cam = cameraOf(chunk, t);

  let from = null;
  let to = null;
  let prev = null;
  for (const entry of entries) {
    if (entry.t <= local) {
      from = prev ? prev.at : entry.at;
      to = entry.at;
      prev = entry;
    } else break;
  }
  if (!prev) {
    from = to = entries[0].at;
  }
  const startMove = prev ? Math.max(0, prev.t - 0.5) : 0;
  const p = prev ? easeInOut(clamp((local - startMove) / 0.5, 0, 1)) : 1;
  const nx = lerp(from[0], to[0], p);
  const ny = lerp(from[1], to[1], p);
  const screen = plateToScreen(cam, nx, ny);

  for (const entry of entries) {
    const age = local - entry.t;
    if (entry.click && age >= 0 && age < 0.5) {
      const rp = age / 0.5;
      ctx.beginPath();
      ctx.arc(screen.x, screen.y, 12 + 26 * rp, 0, Math.PI * 2);
      ctx.strokeStyle = "rgba(" + hexToRgb(P.accent) + "," + (0.7 * (1 - rp)).toFixed(3) + ")";
      ctx.lineWidth = 2;
      ctx.stroke();
    }
  }

  ctx.save();
  ctx.translate(screen.x, screen.y);
  ctx.beginPath();
  ctx.moveTo(0, 0);
  ctx.lineTo(0, 26);
  ctx.lineTo(7, 20);
  ctx.lineTo(12, 31);
  ctx.lineTo(17, 29);
  ctx.lineTo(12, 18);
  ctx.lineTo(21, 18);
  ctx.closePath();
  ctx.fillStyle = P.cursorFill;
  ctx.shadowColor = "rgba(0,0,0,0.45)";
  ctx.shadowBlur = 6;
  ctx.fill();
  ctx.shadowColor = "transparent";
  ctx.strokeStyle = P.cursorStroke;
  ctx.lineWidth = 1.6;
  ctx.stroke();
  ctx.restore();
}

// ------------------------------------------------------------------ scenes
function sceneBackground() {
  const gradient = ctx.createLinearGradient(0, 0, 0, H);
  gradient.addColorStop(0, P.bg[0]);
  gradient.addColorStop(0.55, P.bg[1]);
  gradient.addColorStop(1, P.bg[2]);
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, W, H);

  ctx.strokeStyle = P.grid;
  ctx.lineWidth = 1;
  for (let x = 120; x < W; x += 120) {
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, H);
    ctx.stroke();
  }
  for (let y = 120; y < H; y += 120) {
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(W, y);
    ctx.stroke();
  }
}

function drawFolderGlyph(x, y, w, h, color) {
  ctx.beginPath();
  ctx.moveTo(x, y + h * 0.24);
  ctx.lineTo(x, y + 3);
  ctx.quadraticCurveTo(x, y, x + 3, y);
  ctx.lineTo(x + w * 0.4, y);
  ctx.lineTo(x + w * 0.5, y + h * 0.24);
  ctx.lineTo(x + w, y + h * 0.24);
  ctx.lineTo(x + w, y + h - 3);
  ctx.quadraticCurveTo(x + w, y + h, x + w - 3, y + h);
  ctx.lineTo(x + 3, y + h);
  ctx.quadraticCurveTo(x, y + h, x, y + h - 3);
  ctx.closePath();
  ctx.fillStyle = color;
  ctx.fill();
}

function drawFolderCard(x, y, w, h, label, alpha, glow, kind) {
  ctx.save();
  ctx.globalAlpha = alpha;
  if (glow) {
    ctx.shadowColor = P.cardGlow;
    ctx.shadowBlur = 34;
  }
  fillRound(x, y, w, h, 12, kind === "unknown" ? P.cardUnknown : P.card);
  ctx.shadowColor = "transparent";
  fillRound(x + 14, y - 12, 74, 22, 6, P.cardTab);
  strokeRound(x, y, w, h, 12, glow ? P.cardGlowStroke : P.cardStroke, glow ? 2 : 1);

  if (kind === "shortcut") {
    ctx.beginPath();
    ctx.arc(x + w - 26, y + h - 26, 12, 0, Math.PI * 2);
    ctx.strokeStyle = P.accentStroke;
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(x + w - 30, y + h - 22);
    ctx.lineTo(x + w - 22, y + h - 30);
    ctx.moveTo(x + w - 22, y + h - 30);
    ctx.lineTo(x + w - 28, y + h - 30);
    ctx.moveTo(x + w - 22, y + h - 30);
    ctx.lineTo(x + w - 22, y + h - 24);
    ctx.stroke();
    fillRound(x + 20, y + 22, 34, 26, 4, P.accentSoft);
    drawText("快捷方式", x + 20, y + h - 20, 18, P.inkFaint);
  } else if (kind === "unknown") {
    fillRound(x + 20, y + 20, 34, 26, 4, THEME === "light" ? "rgba(100,116,138,0.12)" : "rgba(143,152,160,0.16)");
    drawText("?", x + 37, y + 41, 22, P.inkFaint, 600, "center");
    drawText("安装包 · 记不清是哪个", x + 20, y + h - 20, 17, P.inkFaint);
  } else {
    drawFolderGlyph(x + 20, y + 20, 36, 28, THEME === "light" ? "rgba(26,115,184,0.28)" : "rgba(102,192,244,0.32)");
  }

  const text = label.length > 20 ? label.slice(0, 19) + "…" : label;
  drawText(text, x + 20, y + h - (kind === "folder" ? 22 : 46), 20, kind === "unknown" ? P.inkFaint : P.inkSoft);
  ctx.restore();
}

const FOLDER_CARDS = [
  [140, 196, "D:\\Games\\Old"],
  [470, 136, "D:\\Backup\\Indie"],
  [800, 262, "E:\\Downloads\\2023"],
  [1122, 172, "桌面\\新建文件夹"],
  [1452, 286, "D:\\Games\\Indie"],
  [244, 586, "迅雷下载\\游戏"],
  [640, 642, "D:\\Games\\Classics"],
  [1030, 562, "F:\\Games\\New"],
  [1432, 618, "桌面\\游戏快捷方式"],
  [878, 400, "D:\\Games\\Old\\start.exe", "shortcut"],
  [1560, 470, "E:\\Downloads\\2023\\setup.exe", "shortcut"],
  [466, 402, "setup_final(3).exe", "unknown"],
];

function sceneProblem(t) {
  sceneBackground();
  const gather = fadeWindow(t, 4.9, 6.0);
  const travel = fadeWindow(t, 6.0, 7.25);
  const burst = fadeWindow(t, 7.15, 7.55);
  const brandIn = fadeWindow(t, 7.55, 8.6);

  // 1. scattered cards drift, all of them start to glow together
  const cardAlpha = 0.94 * (1 - 0.86 * travel);
  FOLDER_CARDS.forEach(([x, y, label, kind], index) => {
    const drift = Math.sin(t * 0.45 + index * 1.7) * 5;
    const appear = clamp((t - index * 0.09) / 0.5, 0, 1);
    const glow = gather > 0.02 && travel < 0.98;
    drawFolderCard(x, y + drift, 220, 132, label, cardAlpha * appear, glow, kind);
    if (t > 5.2 && index % 3 === 0 && travel < 0.4) {
      const ghost = clamp((t - 5.2 - index * 0.12) / 0.8, 0, 1);
      drawFolderCard(x + 58, y + drift + 40, 220, 132, label, 0.18 * ghost * (1 - travel), false, kind);
    }
  });

  if (gather > 0) {
    ctx.save();
    ctx.globalAlpha = 0.35 * gather * (1 - burst);
    const ring = ctx.createRadialGradient(W / 2, 520, 120, W / 2, 520, 760);
    ring.addColorStop(0, P.accentSoft);
    ring.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = ring;
    ctx.fillRect(0, 0, W, H);
    ctx.restore();
  }

  // 2. every card sends a light dot to the centre
  if (travel > 0) {
    FOLDER_CARDS.forEach(([x, y], index) => {
      const local = clamp((travel - index * 0.014) / 0.86, 0, 1);
      if (local <= 0) return;
      const e = easeInOut(local);
      const fromX = x + 110;
      const fromY = y + 66;
      const toX = W / 2;
      const toY = 520;
      const px = lerp(fromX, toX, e);
      const py = lerp(fromY, toY, e) - Math.sin(local * Math.PI) * 74;
      const size = lerp(5.5, 3.2, e);
      ctx.beginPath();
      ctx.arc(px, py, size, 0, Math.PI * 2);
      ctx.fillStyle = P.accentBright;
      ctx.shadowColor = P.accent;
      ctx.shadowBlur = 18;
      ctx.fill();
      ctx.shadowColor = "transparent";

      ctx.beginPath();
      ctx.moveTo(fromX, fromY);
      ctx.lineTo(px, py);
      ctx.strokeStyle = "rgba(" + hexToRgb(P.accent) + "," + (0.16 * (1 - e)).toFixed(3) + ")";
      ctx.lineWidth = 1.4;
      ctx.stroke();
    });
  }

  // 3. the dots merge, then open like a small firework
  if (burst > 0) {
    const core = clamp((t - 7.15) / 0.42, 0, 1);
    const glowRadius = 26 + 120 * easeOut(core);
    const glowAlpha = 0.35 + 0.5 * Math.sin(Math.PI * clamp(core, 0, 1));
    const coreGlow = ctx.createRadialGradient(W / 2, 520, 0, W / 2, 520, glowRadius * 2.2);
    coreGlow.addColorStop(0, "rgba(" + hexToRgb(P.accentBright) + "," + glowAlpha.toFixed(3) + ")");
    coreGlow.addColorStop(0.45, "rgba(" + hexToRgb(P.accent) + "," + (glowAlpha * 0.5).toFixed(3) + ")");
    coreGlow.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = coreGlow;
    ctx.fillRect(W / 2 - 480, 520 - 480, 960, 960);

    for (let i = 0; i < 3; i += 1) {
      const ringP = clamp((t - 7.2 - i * 0.09) / 0.72, 0, 1);
      if (ringP <= 0) continue;
      ctx.beginPath();
      ctx.arc(W / 2, 520, 30 + 520 * easeOut(ringP), 0, Math.PI * 2);
      ctx.strokeStyle = "rgba(" + hexToRgb(P.accent) + "," + (0.55 * (1 - ringP)).toFixed(3) + ")";
      ctx.lineWidth = 2.5 * (1 - ringP) + 0.4;
      ctx.stroke();
    }

    for (let i = 0; i < 34; i += 1) {
      const angle = (i / 34) * Math.PI * 2 + (i % 3) * 0.13;
      const speed = 320 + ((i * 37) % 260);
      const p = clamp((t - 7.22 - (i % 5) * 0.012) / 0.95, 0, 1);
      if (p <= 0) continue;
      const dist = speed * easeOut(p) + 18;
      const px = W / 2 + Math.cos(angle) * dist;
      const py = 520 + Math.sin(angle) * dist * 0.82;
      const size = (i % 4 === 0 ? 3.6 : 2.2) * (1 - p * 0.6);
      ctx.beginPath();
      ctx.arc(px, py, Math.max(0.4, size), 0, Math.PI * 2);
      ctx.fillStyle = "rgba(" + hexToRgb(P.accentBright) + "," + (0.95 * (1 - p)).toFixed(3) + ")";
      ctx.fill();
    }
  }

  // 4. brand card, with the product icon
  if (brandIn > 0) {
    const veil = ctx.createRadialGradient(W / 2, 500, 80, W / 2, 500, 720);
    veil.addColorStop(0, hexA(P.bg[1], 0.94 * brandIn));
    veil.addColorStop(0.62, hexA(P.bg[1], 0.82 * brandIn));
    veil.addColorStop(1, hexA(P.bg[1], 0));
    ctx.fillStyle = veil;
    ctx.fillRect(0, 0, W, H);

    ctx.save();
    ctx.globalAlpha = brandIn;
    const lift = (1 - brandIn) * 22;
    const icon = PLATES.get("__icon");
    const iconSize = 96;
    if (icon) {
      const ix = W / 2 - iconSize / 2;
      roundRect(ix, 286 + lift, iconSize, iconSize, 22);
      ctx.save();
      ctx.shadowColor = THEME === "light" ? "rgba(26,115,184,0.28)" : "rgba(102,192,244,0.35)";
      ctx.shadowBlur = 36;
      ctx.clip();
      ctx.drawImage(icon, ix, 286 + lift, iconSize, iconSize);
      ctx.restore();
    }
    drawText("GameLibrary", W / 2, 496 + lift, 92, P.brandText, 700, "center");

    const chip = (COPY.brand && COPY.brand.versionChip) || "v1.7.22 · 开发版演示";
    ctx.font = "400 30px " + FONT;
    const chipWidth = ctx.measureText(chip).width + 44;
    fillRound(W / 2 - chipWidth / 2, 540 + lift, chipWidth, 52, 26, P.chipBg);
    strokeRound(W / 2 - chipWidth / 2, 540 + lift, chipWidth, 52, 26, P.chipStroke);
    drawText(chip, W / 2, 575 + lift, 30, P.chipText, 400, "center");
    drawText("把散落的游戏整理到一处，把准备工作交给工具", W / 2, 664 + lift, 34, P.brandSub, 400, "center");
    ctx.restore();
  }
}

function hexA(hex, alpha) {
  return "rgba(" + hexToRgb(hex) + "," + alpha.toFixed(3) + ")";
}

function sceneFolderPanel(t) {
  sceneBackground();
  const panelX = 470;
  const panelY = 236;
  const panelW = 980;
  const panelH = 430;

  ctx.save();
  ctx.shadowColor = P.panelShadow;
  ctx.shadowBlur = 40;
  fillRound(panelX, panelY, panelW, panelH, 18, P.panel);
  ctx.restore();
  strokeRound(panelX, panelY, panelW, panelH, 18, P.panelStroke, 1.5);

  drawText("选择游戏库目录", panelX + 32, panelY + 52, 32, P.ink, 600);
  drawText("系统文件夹选择器", panelX + 32, panelY + 84, 18, P.inkFaint);
  drawText("×", panelX + panelW - 42, panelY + 56, 30, P.inkFaint, 400, "center");

  fillRound(panelX + 32, panelY + 104, panelW - 64, 58, 8, P.field);
  strokeRound(panelX + 32, panelY + 104, panelW - 64, 58, 8, P.fieldStroke);
  const pathIn = clamp((t - 1.2) / 0.4, 0, 1);
  if (pathIn > 0) {
    ctx.save();
    ctx.globalAlpha = pathIn;
    drawText("D:\\Games\\Library", panelX + 52, panelY + 142, 26, P.ink);
    ctx.restore();
  }

  const rows = [
    ["Classics", "9 个游戏"],
    ["Library", "3 个游戏"],
  ];
  rows.forEach(([name, meta], index) => {
    const y = panelY + 190 + index * 84;
    const active = index === 1;
    fillRound(panelX + 32, y, panelW - 64, 68, 10, active ? P.rowActive : P.row);
    strokeRound(panelX + 32, y, panelW - 64, 68, 10, active ? P.rowActiveStroke : P.panelStroke);
    fillRound(panelX + 50, y + 20, 30, 24, 4, active ? P.accent : P.rowIcon);
    drawText(name, panelX + 98, y + 44, 26, P.ink, 500);
    drawText(meta, panelX + panelW - 64, y + 44, 22, P.inkFaint, 400, "right");
  });

  const fly = clamp((t - 0.3) / 0.9, 0, 1);
  if (fly < 1) {
    const e = easeOut(fly);
    const x = lerp(1330, panelX + 300, e);
    const y = lerp(150, panelY + 118, e);
    const scale = lerp(0.55, 0.22, e);
    ctx.save();
    ctx.globalAlpha = 1 - Math.pow(fly, 3);
    ctx.translate(x, y);
    ctx.scale(scale, scale);
    drawFolderCard(0, 0, 220, 132, "D:\\Games\\Library", 1, true, "folder");
    ctx.restore();
  }

  const confirmIn = clamp((t - 1.7) / 0.4, 0, 1);
  if (confirmIn > 0) {
    ctx.save();
    ctx.globalAlpha = confirmIn;
    const cx = panelX + panelW - 60;
    const cy = panelY + panelH - 44;
    ctx.beginPath();
    ctx.arc(cx, cy, 17, 0, Math.PI * 2);
    ctx.fillStyle = P.goodSoft;
    ctx.fill();
    ctx.strokeStyle = P.goodStroke;
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(cx - 7, cy);
    ctx.lineTo(cx - 1, cy + 6);
    ctx.lineTo(cx + 8, cy - 6);
    ctx.strokeStyle = P.good;
    ctx.lineWidth = 3;
    ctx.stroke();
    drawText("已选择该目录", panelX + 32, panelY + panelH - 36, 20, P.good, 500);
    ctx.restore();
  }
}

function sceneFlowchart(t) {
  sceneBackground();
  sceneChapter("流程示意图", t, 0.2, 4.4);
  const nodes = [
    ["兼容检查", "识别引擎与版本"],
    ["插件准备", "准备插件文件"],
    ["配置写入", "写入游戏目录"],
  ];
  const nodeW = 460;
  const nodeH = 168;
  const gap = 90;
  const totalW = nodes.length * nodeW + (nodes.length - 1) * gap;
  const startX = (W - totalW) / 2;
  const y = 430;
  const active = Math.min(nodes.length - 1, Math.floor(clamp((t - 0.5) / 1.15, 0, 2.999)));

  nodes.forEach(([title, sub], index) => {
    const x = startX + index * (nodeW + gap);
    const done = index < active;
    const isActive = index === active;
    fillRound(x, y, nodeW, nodeH, 16, isActive ? P.nodeActive : P.node);
    strokeRound(x, y, nodeW, nodeH, 16, isActive ? P.cardGlowStroke : P.nodeStroke, isActive ? 2 : 1);

    ctx.beginPath();
    ctx.arc(x + 46, y + 58, 22, 0, Math.PI * 2);
    ctx.fillStyle = done ? P.goodSoft : isActive ? P.accentSoft : (THEME === "light" ? "rgba(100,116,138,0.10)" : "rgba(143,152,160,0.14)");
    ctx.fill();
    ctx.strokeStyle = done ? P.good : isActive ? P.accent : P.panelStroke;
    ctx.lineWidth = 1.5;
    ctx.stroke();
    if (done) {
      ctx.beginPath();
      ctx.moveTo(x + 39, y + 58);
      ctx.lineTo(x + 44, y + 64);
      ctx.lineTo(x + 53, y + 51);
      ctx.strokeStyle = P.good;
      ctx.lineWidth = 2.5;
      ctx.stroke();
    } else {
      drawText(String(index + 1), x + 46, y + 65, 20, isActive ? P.accentBright : P.inkFaint, 600, "center");
    }
    drawText(title, x + 84, y + 68, 36, P.ink, 600);
    drawText(sub, x + 32, y + 126, 23, P.inkFaint);

    if (index < nodes.length - 1) {
      const ax = x + nodeW + 22;
      const ay = y + nodeH / 2;
      ctx.beginPath();
      ctx.moveTo(ax, ay);
      ctx.lineTo(ax + gap - 44, ay);
      ctx.strokeStyle = index < active ? P.goodStroke : P.accentStroke;
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(ax + gap - 44, ay);
      ctx.lineTo(ax + gap - 54, ay - 6);
      ctx.moveTo(ax + gap - 44, ay);
      ctx.lineTo(ax + gap - 54, ay + 6);
      ctx.strokeStyle = index < active ? P.goodStroke : P.accentStroke;
      ctx.lineWidth = 2;
      ctx.stroke();
    }
  });
}

function sceneCollapse(t) {
  sceneBackground();
  const cards = [
    ["#1f3a2e", 300, 300], ["#2b2140", 640, 300], ["#3a2320", 980, 300], ["#1d2f3d", 1320, 300],
    ["#372b1c", 300, 560], ["#22352b", 640, 560], ["#2a2438", 980, 560], ["#37222c", 1320, 560],
  ];
  const p = easeInOut(clamp(t / 2.2, 0, 1));
  ctx.save();
  cards.forEach(([color, x, y], index) => {
    const scale = lerp(1, 0.18, p);
    const tx = lerp(x, W / 2, p);
    const ty = lerp(y, H / 2, p) + Math.sin(index) * 10 * (1 - p);
    ctx.save();
    ctx.translate(tx, ty);
    ctx.scale(scale, scale);
    fillRound(0, 0, 200, 260, 14, color);
    strokeRound(0, 0, 200, 260, 14, P.cardStroke);
    ctx.restore();
  });
  ctx.restore();

  const glow = clamp((t - 1.4) / 0.8, 0, 1);
  if (glow > 0) {
    const gradient = ctx.createRadialGradient(W / 2, H / 2, 10, W / 2, H / 2, 240);
    gradient.addColorStop(0, "rgba(" + hexToRgb(P.accent) + "," + (0.22 * glow).toFixed(3) + ")");
    gradient.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, W, H);
  }
}

// ------------------------------------------------------------------ captions
const SHOW_CAPTIONS = PARAMS.get("captions") !== "0";

function currentSubtitle(t) {
  for (const subtitle of SUBTITLES) {
    if (t >= subtitle.start && t < subtitle.end) return subtitle;
  }
  return null;
}

function drawCaption(t) {
  if (!SHOW_CAPTIONS) return;
  const subtitle = currentSubtitle(t);
  if (!subtitle) return;
  const alpha = Math.min(1, (t - subtitle.start) / 0.25, (subtitle.end - t) / 0.25);
  if (alpha <= 0) return;

  const scrim = ctx.createLinearGradient(0, 790, 0, H);
  scrim.addColorStop(0, OVERLAY.caption[0]);
  scrim.addColorStop(0.32, OVERLAY.caption[1]);
  scrim.addColorStop(1, OVERLAY.caption[2]);
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.fillStyle = scrim;
  ctx.fillRect(0, 790, W, H - 790);

  ctx.font = "600 42px " + FONT;
  const lines = wrapText(subtitle.text, 1560, 2);
  const baseY = lines.length > 1 ? 968 : 1024;
  lines.forEach((line, index) => {
    const y = baseY + index * 56;
    ctx.font = "600 42px " + FONT;
    ctx.textAlign = "center";
    ctx.fillStyle = OVERLAY.captionShadow;
    ctx.fillText(line, W / 2 + 2, y + 2);
    ctx.fillStyle = OVERLAY.captionText;
    ctx.fillText(line, W / 2, y);
  });
  ctx.restore();
}

function wrapText(text, maxWidth, maxLines) {
  ctx.font = "600 42px " + FONT;
  const lines = [];
  let line = "";
  for (const char of text) {
    const candidate = line + char;
    if (ctx.measureText(candidate).width > maxWidth && line) {
      lines.push(line);
      line = char;
      if (lines.length === maxLines) break;
    } else {
      line = candidate;
    }
  }
  if (line && lines.length < maxLines) lines.push(line);
  if (lines.length === maxLines) {
    const joined = lines.join("");
    if (joined.length < text.length) {
      let last = lines[maxLines - 1];
      while (ctx.measureText(last + "…").width > maxWidth && last.length > 1) last = last.slice(0, -1);
      lines[maxLines - 1] = last.replace(/[，、]$/, "") + "…";
    }
  }
  return lines;
}

// ------------------------------------------------------------------ labels
// kind: "chapter" -> highlighted subtitle at the top-left
// kind: "tag"     -> highlighted pill pinned to a product element (at: [nx, ny])
function labelVisible(chunk, t) {
  const label = chunk.label;
  if (!label) return null;
  const local = t - chunk.t0;
  if (local < label.from || local > label.to) return null;
  const alpha = Math.min(1, (local - label.from) / 0.35, (label.to - local) / 0.35);
  return alpha > 0 ? alpha : null;
}

function tagWidth(text) {
  ctx.font = "600 27px " + FONT;
  return ctx.measureText(text).width + 66;
}

function drawChapter(text, alpha) {
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.font = "700 34px " + FONT;
  const width = ctx.measureText(text).width + 46;
  fillRound(72, 52, width, 58, 12, P.labelBg);
  fillRound(72, 52, 7, 58, 3.5, P.accent);
  strokeRound(72, 52, width, 58, 12, hexA(P.accent, 0.35), 1);
  drawText(text, 100, 91, 34, P.labelText, 700);
  ctx.restore();
}

function sceneChapter(text, t, from, to) {
  const alpha = Math.min(1, (t - from) / 0.35, (to - t) / 0.35);
  if (alpha > 0) drawChapter(text, alpha);
}

function drawTag(text, nx, ny, cam, alpha, align = "left") {
  if (!text) return;
  const point = plateToScreen(cam, nx, ny);
  const width = tagWidth(text);
  let x = align === "right" ? point.x - width - 14 : point.x + 14;
  x = clamp(x, 24, W - width - 24);
  const y = clamp(point.y - 23, 24, 780);

  ctx.save();
  ctx.globalAlpha = alpha;
  const dotX = clamp(point.x, 20, W - 20);
  const dotY = clamp(point.y, 20, 860);
  ctx.beginPath();
  ctx.arc(dotX, dotY, 4, 0, Math.PI * 2);
  ctx.fillStyle = P.accent;
  ctx.fill();
  ctx.beginPath();
  ctx.moveTo(dotX, dotY);
  ctx.lineTo(align === "right" ? x + width : x, y + 23);
  ctx.strokeStyle = hexA(P.accent, 0.5);
  ctx.lineWidth = 1.4;
  ctx.stroke();

  ctx.shadowColor = P.panelShadow;
  ctx.shadowBlur = 18;
  fillRound(x, y, width, 46, 23, P.labelBg);
  ctx.shadowColor = "transparent";
  strokeRound(x, y, width, 46, 23, hexA(P.accent, 0.75), 1.5);
  fillRound(x + 16, y + 15, 7, 16, 3.5, P.accent);
  drawText(text, x + 34, y + 31, 27, P.labelText, 600);
  ctx.restore();
}

function drawLabel(chunk, t) {
  const alpha = labelVisible(chunk, t);
  if (!alpha) return;
  const label = chunk.label;
  if (label.kind === "chapter") {
    drawChapter(label.text, alpha);
  } else if (label.kind === "tag" && label.at) {
    drawTag(label.text, label.at[0], label.at[1], cameraOf(chunk, t), alpha, label.align);
  }
}

function hexToRgb(hex) {
  const value = hex.replace("#", "");
  return [parseInt(value.slice(0, 2), 16), parseInt(value.slice(2, 4), 16), parseInt(value.slice(4, 6), 16)].join(",");
}

function drawVignette() {
  if (OVERLAY === LIGHT) {
    const gradient = ctx.createRadialGradient(W / 2, H / 2, H * 0.5, W / 2, H / 2, H * 1.15);
    gradient.addColorStop(0, "rgba(0,0,0,0)");
    gradient.addColorStop(1, OVERLAY.vignette);
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, W, H);
    return;
  }
  const gradient = ctx.createRadialGradient(W / 2, H / 2, H * 0.45, W / 2, H / 2, H * 1.05);
  gradient.addColorStop(0, "rgba(0,0,0,0)");
  gradient.addColorStop(1, P.vignette);
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, W, H);
}

// ------------------------------------------------------------------ theme toggle
function sceneThemeToggle(t) {
  const baseImage = PLATES.get("c-library");
  const otherImage = CROSS_PLATES.get("c-library");
  // Short eased crossfades instead of hard cuts: a one-frame dark->light cut measures
  // as a ~100/255 average-luma jump, which reads as a flash. 0.26 s keeps it calm.
  const blendIn = clamp((t - 0.9) / 0.26, 0, 1);
  const blendOut = clamp((t - 2.3) / 0.26, 0, 1);
  const mix = blendIn - blendOut;
  // One neutral (dark) scrim for the whole toggle, in both builds: switching the
  // palette mid-blend produced a second ~40/255 luma step that read as a flicker.
  OVERLAY = DARK;
  if (baseImage) ctx.drawImage(baseImage, 0, 0, W, H);
  const eased = mix <= 0 ? 0 : mix >= 1 ? 1 : smooth(mix);
  if (otherImage && eased > 0) {
    ctx.save();
    ctx.globalAlpha = eased;
    ctx.drawImage(otherImage, 0, 0, W, H);
    ctx.restore();
  }

  const flashes = [1.05, 2.45];
  for (const at of flashes) {
    const age = t - at;
    if (age < 0 || age > 0.34) continue;
    const p = age / 0.34;
    ctx.save();
    ctx.globalAlpha = 0.32 * (1 - p);
    ctx.fillStyle = P.accent;
    ctx.fillRect(0, 0, W, H);
    ctx.restore();
  }

  const cam = { x: 0, y: 0, size: 1 };
  const point = plateToScreen(cam, 0.8834, 0.029);
  drawClickRing(point.x, point.y, t - 0.55);
  drawClickRing(point.x, point.y, t - 1.95);
  drawPointer(point.x, point.y);
}
function drawClickRing(x, y, age) {
  if (age < 0 || age > 0.55) return;
  const p = age / 0.55;
  ctx.beginPath();
  ctx.arc(x, y, 12 + 30 * p, 0, Math.PI * 2);
  ctx.strokeStyle = "rgba(" + hexToRgb(P.accent) + "," + (0.75 * (1 - p)).toFixed(3) + ")";
  ctx.lineWidth = 2.4;
  ctx.stroke();
}

function drawPointer(x, y) {
  ctx.save();
  ctx.translate(x, y);
  ctx.beginPath();
  ctx.moveTo(0, 0);
  ctx.lineTo(0, 26);
  ctx.lineTo(7, 20);
  ctx.lineTo(12, 31);
  ctx.lineTo(17, 29);
  ctx.lineTo(12, 18);
  ctx.lineTo(21, 18);
  ctx.closePath();
  ctx.fillStyle = P.cursorFill;
  ctx.shadowColor = "rgba(0,0,0,0.45)";
  ctx.shadowBlur = 6;
  ctx.fill();
  ctx.shadowColor = "transparent";
  ctx.strokeStyle = P.cursorStroke;
  ctx.lineWidth = 1.6;
  ctx.stroke();
  ctx.restore();
}

// ------------------------------------------------------------------ chunk
function drawChunk(chunk, t) {
  OVERLAY = P;
  window.__lastDrawn = { id: chunk.id, scene: chunk.scene || null, plate: chunk.plate || null, t };
  if (chunk.scene === "problem") sceneProblem(t);
  else if (chunk.scene === "folder-panel") sceneFolderPanel(t - chunk.t0);
  else if (chunk.scene === "flowchart") sceneFlowchart(t - chunk.t0);
  else if (chunk.scene === "collapse") sceneCollapse(t - chunk.t0);
  else if (chunk.scene === "theme-toggle") sceneThemeToggle(t - chunk.t0);
  else if (chunk.plate) {
    drawPlate(chunk, t);
    drawCursor(chunk, t);
  }
  drawLabel(chunk, t);
}

function chunkAt(t) {
  for (let i = 0; i < CHUNKS.length; i += 1) {
    if (t >= CHUNKS[i].t0 && t < CHUNKS[i].t1) return i;
  }
  return CHUNKS.length - 1;
}

function renderAt(time) {
  const t = clamp(Number(time) || 0, 0, FILM.film.duration - 0.0001);
  ctx.setTransform(DSF, 0, 0, DSF, 0, 0);
  ctx.globalAlpha = 1;
  ctx.fillStyle = P.bg[2];
  ctx.fillRect(0, 0, W, H);

  const index = chunkAt(t);
  const chunk = CHUNKS[index];
  const fade = chunk.fadeIn || 0;

  if (fade > 0 && t < chunk.t0 + fade) {
    const previous = CHUNKS[Math.max(0, index - 1)];
    drawChunk(previous, Math.min(previous.t1 - 0.001, previous.t0 + (previous.t1 - previous.t0)));
    const alpha = smooth(clamp((t - chunk.t0) / fade, 0, 1));
    ctx.globalAlpha = alpha;
    drawChunk(chunk, t);
    ctx.globalAlpha = 1;
  } else {
    drawChunk(chunk, t);
  }

  drawVignette();
  drawCaption(t);
  return true;
}

window.renderAt = renderAt;
window.__debugScenes = { sceneProblem, drawCaption, SUBTITLES, CHUNKS, drawFolderCard };

window.__FILM_READY__ = true;
window.__FILM_INFO__ = {
  theme: THEME,
  duration: FILM.film.duration,
  fps: FILM.film.fps,
  chunks: CHUNKS.map((chunk) => [chunk.id, chunk.t0, chunk.t1]),
  plates: plateNames,
  clicks: CHUNKS.flatMap((chunk) =>
    (chunk.cursor || []).filter((entry) => entry.click).map((entry) => +(chunk.t0 + entry.t).toFixed(3)),
  ),
};
renderAt(0);




