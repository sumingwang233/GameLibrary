import { execFileSync, spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
import net from "node:net";
import path from "node:path";

const [, , executableArgument, dataDirectoryArgument, screenshotArgument, gameDirectoryArgument] = process.argv;
if (!executableArgument || !dataDirectoryArgument || !screenshotArgument) {
  throw new Error("usage: node verify_release.mjs <desktop.exe> <data-dir> <screenshot.png> [game-directory]");
}

const executable = path.resolve(executableArgument);
const dataDirectory = path.resolve(dataDirectoryArgument);
const screenshotPath = path.resolve(screenshotArgument);

const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      server.close((error) => (error ? reject(error) : resolve(port)));
    });
  });
}

async function waitForTarget(port) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json`);
      const targets = await response.json();
      const target = targets.find((item) =>
        item.type === "page"
        && item.webSocketDebuggerUrl
        && item.url?.startsWith("http://tauri.localhost"));
      if (target) return target;
    } catch {
      // The WebView starts after the native window; retry until the deadline.
    }
    await sleep(200);
  }
  throw new Error("release WebView did not expose its debug target within 30 seconds");
}

function cdp(webSocketUrl) {
  const socket = new WebSocket(webSocketUrl);
  const pending = new Map();
  let requestId = 0;

  socket.addEventListener("message", ({ data }) => {
    const message = JSON.parse(data);
    if (message.id && pending.has(message.id)) {
      pending.get(message.id)(message);
      pending.delete(message.id);
    }
  });

  const ready = new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });

  return {
    async send(method, params = {}) {
      await ready;
      return new Promise((resolve, reject) => {
        const id = ++requestId;
        const timeout = setTimeout(() => {
          pending.delete(id);
          reject(new Error(`CDP ${method} timed out`));
        }, 15_000);
        pending.set(id, (message) => {
          clearTimeout(timeout);
          if (message.error) reject(new Error(`${method}: ${message.error.message}`));
          else resolve(message.result);
        });
        socket.send(JSON.stringify({ id, method, params }));
      });
    },
    close() {
      socket.close();
    },
  };
}

function processTree() {
  const script = [
    "Get-CimInstance Win32_Process |",
    "Select-Object Name,ProcessId,ParentProcessId,ExecutablePath |",
    "ConvertTo-Json -Compress",
  ].join(" ");
  const text = execFileSync("powershell.exe", ["-NoProfile", "-Command", script], {
    encoding: "utf8",
    windowsHide: true,
  }).trim();
  if (!text) return [];
  const parsed = JSON.parse(text);
  return Array.isArray(parsed) ? parsed : [parsed];
}

function descendants(processes, rootPid) {
  const ids = new Set([rootPid]);
  let changed = true;
  while (changed) {
    changed = false;
    for (const process of processes) {
      if (ids.has(process.ParentProcessId) && !ids.has(process.ProcessId)) {
        ids.add(process.ProcessId);
        changed = true;
      }
    }
  }
  return processes.filter((process) => ids.has(process.ProcessId));
}

const port = await freePort();
const webViewData = path.join(path.dirname(dataDirectory), "webview2");
const desktop = spawn(executable, [], {
  cwd: path.dirname(executable),
  env: {
    ...process.env,
    GAMELIBRARY_DATA_DIR: dataDirectory,
    WEBVIEW2_USER_DATA_FOLDER: webViewData,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
  },
  stdio: "ignore",
  windowsHide: true,
});

let client;
try {
  const target = await waitForTarget(port);
  client = cdp(target.webSocketDebuggerUrl);
  const expression = `
    (async () => {
      const deadline = Date.now() + 15000;
      let stylesheet;
      let moduleScript;
      while (Date.now() < deadline) {
        stylesheet = document.querySelector('link[rel="stylesheet"]');
        moduleScript = document.querySelector('script[type="module"][src]');
        if (stylesheet && moduleScript && document.querySelector('#root > *')) break;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      if (!stylesheet || !moduleScript) throw new Error('compiled stylesheet or module script tag is missing after navigation');
      const [cssResponse, jsResponse] = await Promise.all([fetch(stylesheet.href), fetch(moduleScript.src)]);
      const cssText = await cssResponse.text();
      const jsText = await jsResponse.text();
      const firstChild = document.querySelector('#root > *');
      return {
        location: location.href,
        css: { url: stylesheet.href, status: cssResponse.status, type: cssResponse.headers.get('content-type'), length: cssText.length },
        js: { url: moduleScript.src, status: jsResponse.status, type: jsResponse.headers.get('content-type'), length: jsText.length },
        sheetRules: stylesheet.sheet ? stylesheet.sheet.cssRules.length : 0,
        bodyBackground: getComputedStyle(document.body).backgroundColor,
        rootDisplay: firstChild ? getComputedStyle(firstChild).display : null,
        hasTailwind: cssText.includes('tailwindcss'),
      };
    })()
  `;
  let evaluated;
  for (let attempt = 0; attempt < 3; attempt += 1) {
    try {
      evaluated = await client.send("Runtime.evaluate", {
        expression,
        awaitPromise: true,
        returnByValue: true,
      });
      break;
    } catch (error) {
      const navigationRace = String(error).includes("Execution context was destroyed");
      if (!navigationRace || attempt === 2) throw error;

      // WebView2 可能在调试目标刚出现后完成最后一次导航，旧 execution context
      // 会在 Runtime.evaluate 期间被销毁。重新获取目标并有限重试，其他错误仍立即失败。
      client.close();
      await sleep(500);
      const retryTarget = await waitForTarget(port);
      client = cdp(retryTarget.webSocketDebuggerUrl);
    }
  }
  if (!evaluated) throw new Error("release WebView evaluation did not complete");
  if (evaluated.exceptionDetails) throw new Error(evaluated.exceptionDetails.text);
  const result = evaluated.result.value;
  if (gameDirectoryArgument) {
    const opened = await client.send("Runtime.evaluate", {
      expression: `(async () => {
        const invoke = window.__TAURI__.core.invoke;
        await invoke('open_game_directory', { path: ${JSON.stringify(gameDirectoryArgument)} });
        try {
          await invoke('open_game_directory', { path: '.' });
          throw new Error('relative directory was unexpectedly accepted');
        } catch (error) {
          if (!String(error).includes('游戏目录不存在或无法访问')) throw error;
        }
        return { opened: true, relativePathRejected: true };
      })()`,
      awaitPromise: true,
      returnByValue: true,
    });
    if (opened.exceptionDetails) throw new Error(JSON.stringify(opened.exceptionDetails));
    result.directoryOpen = opened.result.value;
  }
  const failures = [];
  if (result.css.status !== 200 || !result.css.type?.startsWith("text/css")) failures.push(`CSS response ${result.css.status} ${result.css.type}`);
  if (result.js.status !== 200 || !result.js.type?.includes("javascript")) failures.push(`JS response ${result.js.status} ${result.js.type}`);
  if (!result.hasTailwind || result.sheetRules < 1) failures.push("compiled Tailwind stylesheet was not applied");
  if (result.bodyBackground !== "rgb(23, 26, 33)") failures.push(`unexpected body background ${result.bodyBackground}`);
  if (result.rootDisplay !== "flex") failures.push(`unexpected app shell display ${result.rootDisplay}`);

  const tree = descendants(processTree(), desktop.pid);
  const consoleHosts = tree.filter((process) => process.Name?.toLowerCase() === "conhost.exe");
  if (consoleHosts.length) {
    failures.push(`console host descendants detected: ${consoleHosts.map((item) => `${item.ProcessId}<-parent:${item.ParentProcessId}`).join(", ")}; tree=${JSON.stringify(tree.map(({ Name, ProcessId, ParentProcessId }) => ({ Name, ProcessId, ParentProcessId })))}`);
  }

  const screenshot = await client.send("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
  writeFileSync(screenshotPath, Buffer.from(screenshot.data, "base64"));

  await client.send("Runtime.evaluate", {
    expression: `window.__TAURI__.core.invoke('bridge_request', { request: { requestId: 'release-smoke-stop', operationId: 'host.stop', parameters: {} } }).catch(() => null)`,
    awaitPromise: true,
    returnByValue: true,
  }).catch(() => null);

  if (failures.length) throw new Error(failures.join("; "));
  console.log(JSON.stringify({ ...result, desktopPid: desktop.pid, processTree: tree.map(({ Name, ProcessId, ParentProcessId }) => ({ Name, ProcessId, ParentProcessId })), screenshotPath }));
} finally {
  client?.close();
  if (!desktop.killed) desktop.kill();
  await sleep(500);
  try {
    execFileSync("taskkill.exe", ["/PID", String(desktop.pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
  } catch {
    // The process tree may already have exited cleanly.
  }
}
