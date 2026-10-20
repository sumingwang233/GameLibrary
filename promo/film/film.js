/**
 * Film stage. Everything visible is a pure function of `t` (seconds):
 *   window.renderAt(t)
 * No timers, no accumulated state, no randomness without a fixed seed.
 *
 * Canvas is 1920x1080 CSS pixels backed by 3840x2160 device pixels, so the
 * frame that Playwright screenshots at 1920x1080 is a clean downsample.
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

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const lerp = (a, b, p) => a + (b - a) * p;
const easeInOut = (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2);
const easeOut = (p) => 1 - Math.pow(1 - p, 3);
const smooth = (p) => p * p * (3 - 2 * p);

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
  loadJSON("../out/plates/plates.json"),
]);

const theme = FILM.theme;
const SUBTITLES = COPY.subtitles || [];
const CHUNKS = SHOTS.chunks;
const BOXES = new Map(PLATE_MANIFEST.map((entry) => [entry.name, entry.boxes || []]));

const plateNames = [...new Set(CHUNKS.map((chunk) => chunk.plate).filter(Boolean))];
const PLATES = new Map();
await Promise.all(
  plateNames.map(async (name) => {
    PLATES.set(name, await loadImage("../out/plates/" + name + ".png"));
  }),
);

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
function drawPlate(chunk, t) {
  const image = PLATES.get(chunk.plate);
  if (!image) return;
  const cam = cameraOf(chunk, t);
  const sw = cam.size * image.width;
  const sh = cam.size * image.height;
  const sx = clamp(cam.x, 0, 1 - cam.size) * image.width;
  const sy = clamp(cam.y, 0, 1 - cam.size) * image.height;
  ctx.drawImage(image, sx, sy, sw, sh, 0, 0, W, H);
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
    if (!entry.target) continue;
    const box = boxOf(entry.target[0], entry.target[1]);
    if (!box) continue;
    entries.push({ t: chunk.t0 + entry.t, x: box.x + box.w / 2, y: box.y + box.h / 2, click: !!entry.click });
  }
  entries.sort((a, b) => a.t - b.t);
  return entries;
}

function drawCursor(chunk, t) {
  const entries = cursorEntries(chunk);
  if (!entries.length) return;
  if (t < chunk.t0 + 0.2) return;

  let index = entries.findIndex((entry) => entry.t >= t);
  if (index === -1) index = entries.length - 1;
  const current = entries[index];
  const previous = index > 0 ? entries[index - 1] : current;
  const moveStart = current.t - 0.5;
  let nx = current.x;
  let ny = current.y;
  if (t < moveStart) {
    nx = previous.x;
    ny = previous.y;
  } else {
    const p = clamp((t - moveStart) / 0.5, 0, 1);
    const e = easeInOut(p);
    nx = lerp(previous.x, current.x, e);
    ny = lerp(previous.y, current.y, e);
  }

  const cam = cameraOf(chunk, t);
  const point = plateToScreen(cam, nx, ny);

  for (const entry of entries) {
    if (!entry.click) continue;
    const age = t - entry.t;
    if (age < 0 || age > 0.5) continue;
    const center = plateToScreen(cam, entry.x, entry.y);
    const p = age / 0.5;
    ctx.beginPath();
    ctx.arc(center.x, center.y, 10 + 30 * easeOut(p), 0, Math.PI * 2);
    ctx.strokeStyle = "rgba(102,192,244," + (0.85 * (1 - p)).toFixed(3) + ")";
    ctx.lineWidth = 3;
    ctx.stroke();
  }

  ctx.save();
  ctx.translate(point.x, point.y);
  ctx.scale(1.25, 1.25);
  ctx.beginPath();
  ctx.moveTo(0, 0);
  ctx.lineTo(0, 17);
  ctx.lineTo(4.4, 12.4);
  ctx.lineTo(9.9, 20.4);
  ctx.lineTo(13.4, 18.4);
  ctx.lineTo(8.1, 10.9);
  ctx.lineTo(14.6, 9.2);
  ctx.closePath();
  ctx.fillStyle = "#ffffff";
  ctx.shadowColor = "rgba(0,0,0,0.55)";
  ctx.shadowBlur = 8;
  ctx.shadowOffsetY = 2;
  ctx.fill();
  ctx.shadowColor = "transparent";
  ctx.strokeStyle = "rgba(9,12,17,0.85)";
  ctx.lineWidth = 1.4;
  ctx.stroke();
  ctx.restore();
}

// ------------------------------------------------------------------ scenes
function sceneBackground(t) {
  const gradient = ctx.createLinearGradient(0, 0, 0, H);
  gradient.addColorStop(0, "#181c24");
  gradient.addColorStop(0.55, "#14171f");
  gradient.addColorStop(1, "#0f1218");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, W, H);

  ctx.strokeStyle = "rgba(120,160,200,0.045)";
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
    ctx.shadowColor = "rgba(102,192,244,0.55)";
    ctx.shadowBlur = 34;
  }
  fillRound(x, y, w, h, 12, kind === "unknown" ? "#202630" : "#1b2838");
  ctx.shadowColor = "transparent";
  fillRound(x + 14, y - 12, 74, 22, 6, "#243447");
  strokeRound(x, y, w, h, 12, glow ? "rgba(102,192,244,0.85)" : "rgba(48,71,94,0.95)", glow ? 2 : 1);

  if (kind === "shortcut") {
    ctx.beginPath();
    ctx.arc(x + w - 26, y + h - 26, 12, 0, Math.PI * 2);
    ctx.strokeStyle = "rgba(102,192,244,0.5)";
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
    fillRound(x + 20, y + 22, 34, 26, 4, "rgba(102,192,244,0.18)");
    drawText("快捷方式", x + 20, y + h - 20, 18, "#8f98a0");
  } else if (kind === "unknown") {
    fillRound(x + 20, y + 20, 34, 26, 4, "rgba(143,152,160,0.16)");
    drawText("?", x + 37, y + 41, 22, "#8f98a0", 600, "center");
    drawText("安装包 · 记不清是哪个", x + 20, y + h - 20, 17, "#8f98a0");
  } else {
    drawFolderGlyph(x + 20, y + 20, 36, 28, "rgba(102,192,244,0.32)");
  }

  const text = label.length > 20 ? label.slice(0, 19) + "…" : label;
  drawText(text, x + 20, y + h - (kind === "folder" ? 22 : 46), 20, kind === "unknown" ? "#8f98a0" : "#c7d5e0");
  ctx.restore();
}

function sceneProblem(t) {
  sceneBackground(t);
  const folders = [
    [140, 196, "D:\\Games\\Old", "folder"],
    [470, 136, "D:\\Backup\\Indie", "folder"],
    [800, 262, "E:\\Downloads\\2023", "folder"],
    [1122, 172, "桌面\\新建文件夹", "folder"],
    [1452, 286, "D:\\Games\\Indie", "folder"],
    [244, 586, "迅雷下载\\游戏", "folder"],
    [640, 642, "D:\\Games\\Classics", "folder"],
    [1030, 562, "F:\\Games\\New", "folder"],
    [1432, 618, "桌面\\游戏快捷方式", "folder"],
    [878, 400, "D:\\Games\\Old\\start.exe", "shortcut"],
    [1560, 470, "E:\\Downloads\\2023\\setup.exe", "shortcut"],
    [466, 402, "setup_final(3).exe", "unknown"],
  ];

  const dimAll = t > 9.4;
  const brandOut = clamp((t - 10.1) / 0.8, 0, 1);
  const brandIn = clamp((t - 10.7) / 0.7, 0, 1);
  const sceneAlpha = 1 - brandOut;

  if (sceneAlpha > 0.01) {
    ctx.save();
    ctx.globalAlpha = sceneAlpha;
    folders.forEach(([x, y, label, kind], index) => {
      const drift = Math.sin(t * 0.45 + index * 1.7) * 5;
      const alpha = dimAll ? (index === 5 ? 0.55 : 0.16) : 0.94;
      drawFolderCard(x, y + drift, 220, 132, label, alpha, dimAll && index === 5, kind);

      if (t > 5.2 && index % 3 === 0) {
        const ghost = clamp((t - 5.2 - index * 0.12) / 0.8, 0, 1);
        drawFolderCard(x + 58, y + drift + 40, 220, 132, label, 0.2 * ghost, false, kind);
      }
    });

    if (t > 5.2 && t < 9.8) {
      const stops = [0, 2, 5, 1, 4, 8];
      const step = 0.62;
      const raw = (t - 5.2) / step;
      const at = Math.min(stops.length - 1, Math.floor(raw));
      const target = folders[stops[at]];
      const pulse = 1 - clamp(raw - at, 0, 1);
      const x = target[0] + 110;
      const y = target[1] + 66 + Math.sin(t * 0.45 + stops[at] * 1.7) * 5;
      ctx.beginPath();
      ctx.arc(x, y, 92, 0, Math.PI * 2);
      ctx.strokeStyle = "rgba(102,192,244," + (0.5 + 0.4 * pulse).toFixed(2) + ")";
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(x, y, 118 + 10 * pulse, 0, Math.PI * 2);
      ctx.strokeStyle = "rgba(102,192,244,0.16)";
      ctx.stroke();
    }
    ctx.restore();
  }

  if (brandIn > 0) {
    const glow = ctx.createRadialGradient(W / 2, 520, 60, W / 2, 520, 640);
    glow.addColorStop(0, "rgba(11,14,19," + (0.92 * brandIn).toFixed(3) + ")");
    glow.addColorStop(1, "rgba(11,14,19,0)");
    ctx.fillStyle = glow;
    ctx.fillRect(0, 0, W, H);

    ctx.save();
    ctx.globalAlpha = brandIn;
    const lift = (1 - brandIn) * 18;
    drawText("GameLibrary", W / 2, 512 + lift, 88, "#ffffff", 700, "center");
    const chip = "v1.7.22 · 开发版演示";
    ctx.font = "400 30px " + FONT;
    const chipWidth = ctx.measureText(chip).width + 44;
    fillRound(W / 2 - chipWidth / 2, 548 + lift, chipWidth, 52, 26, "rgba(102,192,244,0.12)");
    strokeRound(W / 2 - chipWidth / 2, 548 + lift, chipWidth, 52, 26, "rgba(102,192,244,0.45)");
    drawText(chip, W / 2, 583 + lift, 30, "#8ed5f7", 400, "center");
    drawText("把散落的游戏整理到一处", W / 2, 664 + lift, 34, "#8f98a0", 400, "center");
    ctx.restore();
  }
}

function sceneFolderPanel(t) {
  sceneBackground(t);
  const panelX = 470;
  const panelY = 236;
  const panelW = 980;
  const panelH = 430;

  ctx.save();
  ctx.shadowColor = "rgba(0,0,0,0.5)";
  ctx.shadowBlur = 40;
  fillRound(panelX, panelY, panelW, panelH, 18, "#1b2838");
  ctx.restore();
  strokeRound(panelX, panelY, panelW, panelH, 18, "#30475e", 1.5);

  drawText("选择游戏库目录", panelX + 32, panelY + 52, 32, "#c7d5e0", 600);
  drawText("系统文件夹选择器", panelX + 32, panelY + 84, 18, "#8f98a0");
  drawText("×", panelX + panelW - 42, panelY + 56, 30, "#8f98a0", 400, "center");

  fillRound(panelX + 32, panelY + 104, panelW - 64, 58, 8, "#121821");
  strokeRound(panelX + 32, panelY + 104, panelW - 64, 58, 8, "#30475e");
  const pathIn = clamp((t - 1.2) / 0.4, 0, 1);
  if (pathIn > 0) {
    ctx.save();
    ctx.globalAlpha = pathIn;
    drawText("D:\\Games\\Library", panelX + 52, panelY + 142, 26, "#c7d5e0");
    ctx.restore();
  }

  const rows = [
    ["Classics", "9 个游戏"],
    ["Library", "3 个游戏"],
  ];
  rows.forEach(([name, meta], index) => {
    const y = panelY + 190 + index * 84;
    const active = index === 1;
    fillRound(panelX + 32, y, panelW - 64, 68, 10, active ? "rgba(102,192,244,0.12)" : "#1e2a38");
    strokeRound(panelX + 32, y, panelW - 64, 68, 10, active ? "rgba(102,192,244,0.7)" : "#30475e");
    fillRound(panelX + 50, y + 20, 30, 24, 4, active ? "#66c0f4" : "#2a475e");
    drawText(name, panelX + 98, y + 44, 26, "#e6eef5", 500);
    drawText(meta, panelX + panelW - 64, y + 44, 22, "#8f98a0", 400, "right");
  });

  const fly = clamp((t - 0.3) / 0.9, 0, 1);
  if (fly < 1) {
    const e = easeOut(fly);
    const x = lerp(120, panelX + 300, e);
    const y = lerp(780, panelY + 118, e);
    const scale = lerp(0.75, 0.3, e);
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
    ctx.fillStyle = "rgba(164,208,7,0.16)";
    ctx.fill();
    ctx.strokeStyle = "rgba(164,208,7,0.85)";
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(cx - 7, cy);
    ctx.lineTo(cx - 1, cy + 6);
    ctx.lineTo(cx + 8, cy - 6);
    ctx.strokeStyle = "#a4d007";
    ctx.lineWidth = 3;
    ctx.stroke();
    drawText("已选择该目录", panelX + 32, panelY + panelH - 36, 20, "#a4d007", 500);
    ctx.restore();
  }
}

function sceneFlowchart(t) {
  sceneBackground(t);
  const nodes = [
    ["兼容检查", "识别引擎与版本"],
    ["插件准备", "准备插件文件"],
    ["配置写入", "写入游戏目录"],
  ];
  const startX = 210;
  const y = 440;
  const nodeW = 430;
  const gap = 85;
  const active = t < 2.3 ? 0 : t < 4.4 ? 1 : 2;

  nodes.forEach(([title, sub], index) => {
    const x = startX + index * (nodeW + gap);
    const isActive = index === active;
    const done = index < active || (index === 2 && t > 6.2);
    fillRound(x, y, nodeW, 160, 16, isActive ? "#22354a" : "#1b2838");
    strokeRound(x, y, nodeW, 160, 16, isActive ? "rgba(102,192,244,0.85)" : "rgba(48,71,94,0.9)", isActive ? 2 : 1);

    ctx.beginPath();
    ctx.arc(x + 44, y + 52, 17, 0, Math.PI * 2);
    ctx.fillStyle = done ? "rgba(164,208,7,0.18)" : isActive ? "rgba(102,192,244,0.22)" : "rgba(143,152,160,0.14)";
    ctx.fill();
    ctx.strokeStyle = done ? "#a4d007" : isActive ? "#66c0f4" : "#30475e";
    ctx.lineWidth = 2;
    ctx.stroke();
    if (done) {
      ctx.beginPath();
      ctx.moveTo(x + 36, y + 52);
      ctx.lineTo(x + 42, y + 59);
      ctx.lineTo(x + 53, y + 45);
      ctx.strokeStyle = "#a4d007";
      ctx.lineWidth = 3;
      ctx.stroke();
    } else {
      drawText(String(index + 1), x + 44, y + 60, 20, isActive ? "#8ed5f7" : "#8f98a0", 600, "center");
    }
    drawText(title, x + 78, y + 66, 34, "#e6eef5", 600);
    drawText(sub, x + 32, y + 118, 22, "#8f98a0");

    if (index < nodes.length - 1) {
      const ax = x + nodeW + 16;
      const bx = x + nodeW + gap - 18;
      ctx.beginPath();
      ctx.moveTo(ax, y + 80);
      ctx.lineTo(bx, y + 80);
      ctx.strokeStyle = index < active ? "rgba(164,208,7,0.65)" : "rgba(102,192,244,0.45)";
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(bx, y + 80);
      ctx.lineTo(bx - 12, y + 73);
      ctx.lineTo(bx - 12, y + 87);
      ctx.closePath();
      ctx.fillStyle = index < active ? "rgba(164,208,7,0.65)" : "rgba(102,192,244,0.45)";
      ctx.fill();
    }
  });

}

function sceneCollapse(t) {
  sceneBackground(t);
  const cards = [
    ["#1f3a2e", 300, 300],
    ["#2b2140", 640, 300],
    ["#3a2320", 980, 300],
    ["#1d2f3d", 1320, 300],
    ["#372b1c", 300, 560],
    ["#22352b", 640, 560],
    ["#2a2438", 980, 560],
    ["#37222c", 1320, 560],
  ];
  const gather = clamp(t / 0.9, 0, 1);
  const shrink = clamp((t - 1.0) / 1.2, 0, 1);

  cards.forEach(([color, x, y], index) => {
    const e = easeInOut(gather);
    const cx = lerp(x, 960 - 60, e) + (index % 2 ? 6 : -6);
    const cy = lerp(y, 470, e);
    const scale = lerp(1, 0.9, e) * lerp(1, 0.15, easeInOut(shrink));
    ctx.save();
    ctx.globalAlpha = lerp(0.95, 0.0, easeInOut(shrink));
    ctx.translate(cx, cy);
    ctx.scale(scale, scale);
    fillRound(0, 0, 200, 260, 14, color);
    strokeRound(0, 0, 200, 260, 14, "rgba(102,192,244,0.22)");
    ctx.restore();
  });

  const glow = clamp((t - 1.6) / 0.8, 0, 1);
  if (glow > 0) {
    const gradient = ctx.createRadialGradient(960, 470, 10, 960, 470, 260);
    gradient.addColorStop(0, "rgba(102,192,244," + (0.22 * glow).toFixed(3) + ")");
    gradient.addColorStop(1, "rgba(102,192,244,0)");
    ctx.fillStyle = gradient;
    ctx.fillRect(640, 150, 640, 640);
  }
}

// ------------------------------------------------------------------ overlays
function currentSubtitle(t) {
  return SUBTITLES.find((subtitle) => t >= subtitle.start - 0.001 && t <= subtitle.end + 0.001) || null;
}

const SHOW_CAPTIONS = new URLSearchParams(location.search).get("captions") !== "0";

function drawCaption(t) {
  if (!SHOW_CAPTIONS) return;
  const subtitle = currentSubtitle(t);
  if (!subtitle) return;
  const alpha = Math.min(1, (t - subtitle.start) / 0.25, (subtitle.end - t) / 0.25);
  if (alpha <= 0) return;

  const scrim = ctx.createLinearGradient(0, 790, 0, H);
  scrim.addColorStop(0, "rgba(6,9,13,0)");
  scrim.addColorStop(0.32, "rgba(6,9,13,0.62)");
  scrim.addColorStop(1, "rgba(6,9,13,0.9)");
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
    ctx.fillStyle = "rgba(0,0,0,0.55)";
    ctx.fillText(line, W / 2 + 2, y + 2);
    ctx.fillStyle = "#f2f6fa";
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
      lines[maxLines - 1] = last.replace(/[，。、]$/, "") + "…";
    }
  }
  return lines;
}

function drawLabel(chunk, t) {
  const label = chunk.label;
  if (!label) return;
  const local = t - chunk.t0;
  if (local < label.from || local > label.to) return;
  const alpha = Math.min(1, (local - label.from) / 0.3, (label.to - local) / 0.3);
  if (alpha <= 0) return;

  const subtitle = SHOW_CAPTIONS ? currentSubtitle(t) : null;
  const captionLines = subtitle ? wrapText(subtitle.text, 1560, 2).length : 1;
  const baseY = captionLines > 1 ? 900 : 950;

  const tone = label.tone === "warn" ? "#f5c542" : label.tone === "demo" ? "#c7d5e0" : "#66c0f4";

  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.font = "500 28px " + FONT;
  const width = ctx.measureText(label.text).width + 74;
  const x = W / 2 - width / 2;
  const y = baseY - 40;
  fillRound(x, y, width, 46, 23, "rgba(9,13,19,0.95)");
  strokeRound(x, y, width, 46, 23, "rgba(" + hexToRgb(tone) + ",0.62)");
  ctx.beginPath();
  ctx.arc(x + 28, y + 23, 5, 0, Math.PI * 2);
  ctx.fillStyle = tone;
  ctx.fill();
  drawText(label.text, x + 46, y + 32, 28, "#dfe9f2", 500);
  ctx.restore();
}

function hexToRgb(hex) {
  const value = hex.replace("#", "");
  return [parseInt(value.slice(0, 2), 16), parseInt(value.slice(2, 4), 16), parseInt(value.slice(4, 6), 16)].join(",");
}

function drawVignette() {
  const gradient = ctx.createRadialGradient(W / 2, H / 2, H * 0.45, W / 2, H / 2, H * 1.05);
  gradient.addColorStop(0, "rgba(0,0,0,0)");
  gradient.addColorStop(1, "rgba(0,0,0,0.28)");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, W, H);
}

// ------------------------------------------------------------------ chunk
function drawChunk(chunk, t) {
  window.__lastDrawn = { id: chunk.id, scene: chunk.scene || null, plate: chunk.plate || null, t };
  if (chunk.scene === "problem") sceneProblem(t);
  else if (chunk.scene === "folder-panel") sceneFolderPanel(t - chunk.t0);
  else if (chunk.scene === "flowchart") sceneFlowchart(t - chunk.t0);
  else if (chunk.scene === "collapse") sceneCollapse(t - chunk.t0);
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
  ctx.fillStyle = "#0b0e13";
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
  duration: FILM.film.duration,
  fps: FILM.film.fps,
  chunks: CHUNKS.map((chunk) => [chunk.id, chunk.t0, chunk.t1]),
  plates: plateNames,
  clicks: CHUNKS.flatMap((chunk) =>
    (chunk.cursor || []).filter((entry) => entry.click).map((entry) => +(chunk.t0 + entry.t).toFixed(3)),
  ),
};
renderAt(0);

