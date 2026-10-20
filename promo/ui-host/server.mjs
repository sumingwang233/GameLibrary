/* promo/ui-host/server.mjs
 * Static host for the promo film: serves the product's built dist and injects
 * the demo IPC mock before the bundle. Nothing here is part of the product.
 */
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROMO = path.resolve(__dirname, "..");
const REPO = path.resolve(PROMO, "..");
const DIST = path.join(REPO, "src", "GameLibrary.Tauri", "dist");
const COPY = JSON.parse(fs.readFileSync(path.join(PROMO, "config", "copy.json"), "utf8"));
const PORT = Number(process.env.PROMO_PORT || 1421);

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".woff2": "font/woff2",
  ".mp3": "audio/mpeg",
  ".wav": "audio/wav"
};

function send(res, status, type, body) {
  res.writeHead(status, {
    "content-type": type,
    "cache-control": "no-store",
    "access-control-allow-origin": "*"
  });
  res.end(body);
}

function injectDemo(html) {
  const boot = [
    "<script>window.__PROMO_DEMO__=" + JSON.stringify(COPY) + ";</script>",
    '<script src="/__promo__/mock.js"></script>'
  ].join("\n    ");
  return html.replace('<script type="module"', boot + '\n    <script type="module"');
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, "http://127.0.0.1");
  const pathname = decodeURIComponent(url.pathname);

  if (pathname === "/" || pathname === "/index.html") {
    const html = fs.readFileSync(path.join(DIST, "index.html"), "utf8");
    return send(res, 200, TYPES[".html"], injectDemo(html));
  }

  if (pathname === "/__promo__/mock.js") {
    return send(res, 200, TYPES[".js"], fs.readFileSync(path.join(__dirname, "mock.js")));
  }

  if (pathname.startsWith("/promo/")) {
    const file = path.join(PROMO, pathname.slice("/promo/".length));
    if (file.startsWith(PROMO) && fs.existsSync(file) && fs.statSync(file).isFile()) {
      return send(res, 200, TYPES[path.extname(file)] || "application/octet-stream", fs.readFileSync(file));
    }
    return send(res, 404, "text/plain", "not found");
  }

  const file = path.join(DIST, pathname);
  if (file.startsWith(DIST) && fs.existsSync(file) && fs.statSync(file).isFile()) {
    return send(res, 200, TYPES[path.extname(file)] || "application/octet-stream", fs.readFileSync(file));
  }
  return send(res, 404, "text/plain", "not found");
});

// Windows sometimes excludes a port range (Hyper-V), so walk forward until one binds.
let attempt = PORT;
function listen() {
  server.once("error", (error) => {
    if (error.code === "EACCES" || error.code === "EADDRINUSE") {
      attempt += 1;
      if (attempt > PORT + 40) throw error;
      console.log("port " + (attempt - 1) + " unavailable (" + error.code + "), trying " + attempt);
      listen();
    } else {
      throw error;
    }
  });
  server.listen(attempt, "127.0.0.1", () => {
    fs.writeFileSync(path.join(PROMO, "out", "host-port.txt"), String(attempt));
    console.log("promo host http://127.0.0.1:" + attempt + "/  dist=" + DIST);
  });
}
listen();
