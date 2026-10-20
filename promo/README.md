# GameLibrary 宣传片工程

146 秒、1920×1080、60 fps 的产品宣传片工程，同一时间轴出两版：**深色界面版**（主版）与**浅色界面版**。产品画面来自锁定版本的真实前端组件（`src/GameLibrary.Tauri`），在浏览器里用一套隔离的 Tauri IPC 垫片和示例数据运行；概念画面与镜头运动由 Canvas 逐帧绘制。每一帧都是时间 `t` 的纯函数，没有录屏、没有依赖上一帧状态。

```
promo/
  config/      film.json（片长/分辨率/色板/字号）、copy.json（文案、字幕、示例库）、shots.json（镜头表）
  film/        合成器：index.html + film.css + film.js（逐帧渲染）、endcard.html、assets/app-icon.png
  ui-host/     静态服务 + 浏览器侧 Tauri IPC 垫片 mock.js（示例数据、扫描/翻译/Unity/MTool 流程、深浅主题）
  render/      plates.mjs（真实界面状态截图，按主题分开）、endcard.mjs、stills.mjs、stillframe.mjs、render-film.mjs
  audio/       mix.mjs（两遍 loudnorm 混音）、build-plan.mjs（按点击点生成音效表）、final-plan.json
  assets/      音乐与音效（许可见 docs/assets-license.md）
  tools/       ffmpeg/（项目内二进制）、build_srt.mjs、build-storyboard.mjs
  out/         中间产物（已 gitignore）：plates/{dark,light}、frames-test、film、audio
  deliverables/ 交付文本：SRT、旁白稿
  docs/        证据表、分镜表、版本记录、检查报告、许可清单
```

## 环境

- Node.js 24（仅本工程使用），系统 Chrome（Playwright 以 `channel: "chrome"` 调用，不额外下载 Chromium）。
- 依赖锁在 `package.json` / `package-lock.json`（目前只有 `playwright-core@1.64.1`）。
- 视频与音频编码使用 `tools/ffmpeg/ffmpeg.exe`（7.1.1），**不要求系统安装 FFmpeg**。

```bash
cd promo
npm ci
```

## 一、起界面服务

```bash
node ui-host/server.mjs          # 后台常驻；端口写入 out/host-port.txt
```

服务会静态托管 `src/GameLibrary.Tauri/dist`（若 `dist` 不存在，先在产品目录执行 `npm run build`），并把 `__PROMO_DEMO__` 与 `mock.js` 注入 `index.html`。打开 `http://127.0.0.1:<port>/?scene=library&theme=dark` 即可看到示例库。

场景：`library` / `scan` / `translate` / `unity` / `mtool` / `final`；`theme=dark|light`。

## 二、截取界面状态板（按主题）

```bash
node render/plates.mjs                       # 深色，全部场景
node render/plates.mjs --theme=light         # 浅色，全部场景
node render/plates.mjs b c                   # 只跑指定场景
```

产物：`out/plates/<theme>/<name>.png`（2880×1620，1440×810 @2x）与同目录 `plates.json`（每个控件在页面里的归一化 bbox，供光标定位）。两种主题各 32 张。

## 三、生成片尾卡与关键静帧

```bash
node render/endcard.mjs            # dark / light 两张片尾卡 → out/plates/<theme>/k5-endcard.png
node render/stills.mjs             # k1–k5 五张复核静帧 → out/stills/
node render/stillframe.mjs 12 60 90 145        # 指定秒数的单帧（深色）→ out/frames-test/
node render/stillframe.mjs 33 96 --theme=light # 浅色
```

## 四、渲染成片

```bash
node render/render-film.mjs --theme=dark  --captions=1 --out=out/film/dark-sub-silent.mp4
node render/render-film.mjs --theme=dark  --captions=0 --out=out/film/dark-clean-silent.mp4
node render/render-film.mjs --theme=light --captions=1 --out=out/film/light-sub-silent.mp4
node render/render-film.mjs --theme=light --captions=0 --out=out/film/light-clean-silent.mp4
```

逐帧 `renderAt(t)` + CDP 截图，直接 pipe 给项目内 ffmpeg（libx264 / CRF 16 / preset slow / yuv420p / 60 fps）。单版约 5–8 分钟。`--from= --to=` 可只渲一段做检查。

## 五、混音

```bash
node audio/build-plan.mjs                  # 从 shots.json 的点击点生成 final-plan.json（当前 19 个音效）
node audio/mix.mjs audio/final-plan.json   # → out/audio/mix-final.wav + 波形 + 响度报告
```

音乐取 `assets/music/incompetech_Pamgaea.mp3` 第 10 秒起、截 146 秒，2.5 s 淡入 / 3.5 s 淡出；音效按镜头里的真实点击时间落点。两遍 loudnorm 把成片定在 −16 LUFS、真峰值 ≤ −1.5 dBTP。

## 六、合成交付文件

```bash
tools/ffmpeg/ffmpeg.exe -y -i out/film/dark-sub-silent.mp4 -i out/audio/mix-final.wav \
  -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \
  out/film/GameLibrary-promo-1080p60-dark.mp4
```

无字幕母版、浅色版同理，换输入文件即可。

## 七、文本交付物

```bash
node tools/build_srt.mjs          # deliverables/GameLibrary-promo.srt（26 条）
node tools/build-storyboard.mjs   # docs/storyboard.md（从镜头表生成）
```

## 约定

- 文案、时间轴、色板、字号、音量只在 `config/` 与 `copy.json` 里改，场景代码不散落文案。
- 音效时间由 `shots.json` 的 `cursor[].click` 推导，不手写时间码。
- 任何新的产品镜头都应先在 `plates.mjs` 里截出真实状态，再进 `shots.json`；不要用重绘界面冒充产品画面。
- 演示数据全部来自 `mock.js` 的 12 个原创游戏，无个人路径、账号或密钥；模拟服务响应的镜头带屏幕注释。
- 主题：`film.js` 用 `?theme=` 决定 UI 板与 Canvas 色板；深浅切换镜头（`theme-toggle`）在 11.6–15.2 s，两版都会先后显示深色与浅色界面。
