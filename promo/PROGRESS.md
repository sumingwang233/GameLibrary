# PROGRESS

> 续做时先读本文件，已通过的内容不重做。状态以实际输出为准。

## 当前状态

- 阶段：第二版（用户 13 条修改）已渲染、混音、检查，待提交与交付说明。
- 版本：源码 1.7.22（`c6109ae`，未发布）→ 画面标 `v1.7.22 · 开发版演示`。
- 成片（同一 146 秒时间轴，四支）：
  - `out/film/GameLibrary-promo-1080p60-dark.mp4`（深色版，字幕烧录，主版）
  - `out/film/GameLibrary-promo-1080p60-dark-nosub.mp4`（深色无字幕母版）
  - `out/film/GameLibrary-promo-1080p60-light.mp4`（浅色版，字幕烧录）
  - `out/film/GameLibrary-promo-1080p60-light-nosub.mp4`（浅色无字幕母版）
- 旁白：第一版不录制（用户决定），字幕文本即旁白稿。

## 已完成

- [x] 环境能力核实（2026-10-20）：node 24、Chrome 156、ffmpeg 7.1.1（复制进 `tools/ffmpeg/`）；模型可看图、不可试听。
- [x] 版本与发行锁定；证据表 `docs/feature-evidence.md`；版本记录 `docs/version-record.md`。
- [x] 集中配置：`config/film.json`、`config/copy.json`、`config/shots.json`（41 个镜头，含字幕/注释/相机/光标）。
- [x] `ui-host/`：真实前端 `dist` 静态服务 + `mock.js` 浏览器侧 Tauri IPC（场景 library/scan/translate/unity/mtool/final，12 个原创示例游戏，`theme=dark|light`）。
- [x] 界面状态板：`render/plates.mjs` → 每主题 32 张 2880×1620 素材板 + `plates.json` 控件 bbox（`out/plates/dark|light/`）。
- [x] 片尾卡：`film/endcard.html` + `render/endcard.mjs`（深/浅两版，App 原生图标，URL + 版本 + 许可/署名）。
- [x] 关键静帧：`render/stills.mjs` → k1–k5（库、译名前后、Unity 配置、MTool、片尾）。
- [x] 合成器：`film/film.js` 逐帧 `renderAt(t)`；概念场景（problem + 光点归拢/炸开、theme-toggle、folder-panel、flowchart）+ 产品素材板 + 相机 + 光标 + 字幕 + 章节/标签注释；`?captions=0` 出无字幕母版，`?theme=` 出两版。
- [x] 配乐/音效：Pamgaea（CC BY 4.0）第 10 s 起 146 s；12 个点击 + 7 个转场音效，时间由 `shots.json` 推导（`audio/build-plan.mjs`）。
- [x] 混音：`audio/mix.mjs` 两遍 loudnorm → `out/audio/mix-final.wav`（−15.9 LUFS、真峰值 −4.2 dBTP、146.000 s）。
- [x] 整片渲染：`render/render-film.mjs --theme=dark|light --captions=0|1`（四支）。
- [x] 客观检查：`tools/verify.mjs` → `out/check/verify.json`（时长/帧数/像素格式/黑帧/逐帧亮度跳变/响度）。
- [x] 文本：`deliverables/GameLibrary-promo.srt`（26 条）、`docs/narration-and-subtitles.md`、`deliverables/narration.md`、`docs/storyboard.md`（均由生成脚本产出）。

## 待办

- [ ] `deliverables/` 关键静帧与缩略图表更新为第二版。
- [ ] 检查报告 `docs/check-report.md` 写入第二版测量与肉眼结论。
- [ ] git 提交 `promo/` 源码与文本（不提交 MP4/WAV/帧序列）。

## 偏离与决策记录

- 第一版不加旁白（用户决定）；没有用静音冒充，旁白稿与补录流程单独交付。
- 第二版按用户要求：片长缩到 146 s；开头演示深/浅切换；A 结尾改为“全部文件夹一起高亮 → 光点汇入 → 炸开 → 品牌卡”；标签改为贴在高亮位置；删除 1:26 / 1:42 两处多余标签；流程图标签改「流程示意图」；片尾重排；出浅色版。
- 审核段不展示“审核阶段编辑名称/启动项”（源码无此能力）：审核列表核对 + 入库后详情页查看启动方式。
- 收藏无单卡入口，镜头走“选择卡片 → 批量收藏”，台词不暗示单卡一键。
- 系统级目录选择器不在页面内：影片层重绘目录面板，标「选择文件夹 · 流程演示」。
- 产品界面用真实组件 + IPC 垫片 + 示例数据；Unity/MTool 只停在“已配置/已生成配方，尚未验证游戏内翻译效果”。
- 结尾“启动”不做真实进程：G6 镜头推进到「开始游戏」按钮后切片尾，不伪造系统提示。
- 深浅切换：两张真实截图之间用 0.26 s 淡切（硬切实测单帧亮度跳变 ~100/255，像闪屏）；切换段统一用深色字幕底，避免中途换色造成第二次跳变。
- 发现产品现状：`全部游戏` 会过滤掉 `played=true`（`useGamesQuery.ts:55` 传 `played:false`），因此示例库全部 `played=false`；未修改产品代码。
- 合成器曾出现全黑首帧：`film.json` 结构是 `{film:{…}}`，代码误用 `FILM.duration`；改为 `FILM.film.duration`。
