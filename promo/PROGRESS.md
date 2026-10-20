# PROGRESS

> 续做时先读本文件，已通过的内容不重做。状态以实际输出为准。

## 当前状态

- 阶段：整片已渲染并混音，进行成片检查与交付整理。
- 版本：源码 1.7.22（`c6109ae`，未发布）→ 画面标 `v1.7.22 · 开发版演示`。
- 成片：`out/film/GameLibrary-promo-1080p60.mp4`（字幕烧录）、`…-nosub.mp4`（无字幕母版）。
- 旁白：第一版不录制（用户决定），字幕文本即旁白稿。

## 已完成

- [x] 环境能力核实（2026-10-20）：node 24、Chrome 156、ffmpeg 7.1.1（复制进 `tools/ffmpeg/`）；模型可看图、不可试听。
- [x] 版本与发行锁定；证据表 `docs/feature-evidence.md`；版本记录 `docs/version-record.md`。
- [x] 集中配置：`config/film.json`、`config/copy.json`、`config/shots.json`（44 个镜头，含字幕/注释/相机/光标）。
- [x] `ui-host/`：真实前端 `dist` 静态服务 + `mock.js` 浏览器侧 Tauri IPC（场景 library/scan/translate/unity/mtool/final，12 个原创示例游戏）。
- [x] 界面状态板：`render/plates.mjs` → 31 张 2880×1620 素材板 + `plates.json` 控件 bbox。
- [x] 关键静帧：`render/stills.mjs` → k1–k5（库、译名前后、Unity 配置、MTool、片尾）。
- [x] 合成器：`film/film.js` 逐帧 `renderAt(t)`，概念场景（problem / folder-panel / flowchart）+ 产品素材板 + 相机 + 光标 + 字幕 + 注释；`?captions=0` 出无字幕母版。
- [x] 配乐/音效：Pamgaea（CC BY 4.0）第 10 s 起 150 s；13 个点击 + 7 个转场音效，时间由 `shots.json` 推导（`audio/build-plan.mjs`）。
- [x] 混音：`audio/mix.mjs` 两遍 loudnorm → `out/audio/mix-final.wav`（−15.9 LUFS、真峰值 −4.0 dBTP、150.000 s）。
- [x] 整片渲染：`render/render-film.mjs`（两版各 9000 帧，约 36 fps / 22 fps）。
- [x] SRT：`deliverables/GameLibrary-promo.srt`（20 条，`tools/build_srt.mjs`）。
- [x] 分镜表：`docs/storyboard.md`（由镜头表生成）。
- [x] 旁白稿：`deliverables/narration.md`（含读法提示与补录流程）。

## 进行中

- [ ] 成片客观检查：暗场/黑帧、逐帧亮度跳变、响度、字幕与无字幕版差异、结尾卡停留。
- [ ] `docs/check-report.md`、`docs/assets-license.md` 复核、缩略图表。

## 待办

- [ ] 缩略图表（contact sheet）落盘到 `deliverables/`。
- [ ] 把成片与关键静帧复制到交付目录（MP4 不入 git，仅留在 `out/` 与本地交付路径）。
- [ ] git 提交 `promo/` 源码与文本（不提交 MP4/WAV/帧序列）。

## 偏离与决策记录

- 第一版不加旁白（用户决定）；没有用静音冒充，旁白稿与补录流程单独交付。
- 交付两版：字幕烧录版 + 无字幕母版（用户决定）。
- 审核段不展示“审核阶段编辑名称/启动项”（源码无此能力）：审核列表核对 + 入库后详情页查看启动方式。
- 收藏无单卡入口，镜头走“选择卡片 → 批量收藏”，台词不暗示单卡一键。
- 系统级目录选择器不在页面内：影片层重绘目录面板，标「选择文件夹 · 流程演示」。
- 产品界面用真实组件 + IPC 垫片 + 示例数据；Unity/MTool 只停在“已配置/已生成配方，尚未验证游戏内翻译效果”。
- 结尾“启动”不做真实进程：G6 镜头推进到「开始游戏」按钮后切片尾，不伪造系统提示。
- 发现产品现状：`全部游戏` 会过滤掉 `played=true`（`useGamesQuery.ts:55` 传 `played:false`），因此示例库全部 `played=false`；未修改产品代码。
- 合成器曾出现全黑首帧：`film.json` 结构是 `{film:{…}}`，代码误用 `FILM.duration`；改为 `FILM.film.duration`。

## 缺失 / 待人工确认

- 音频试听：模型无法试听，音乐与音效观感需人工确认（响度与真峰值已客观测量）。
- 旁白：本版未录制；读音检查不适用。
- Unity 游戏内翻译效果：产品侧亦未验收，片中未演示。

## 2026-10-20 收尾状态

- 成片已出：`out/film/GameLibrary-promo-1080p60.mp4`（字幕烧录，66.1 MB）与 `out/film/GameLibrary-promo-1080p60-nosub.mp4`（65.5 MB），均 150.000 s / 9000 帧 / 1920×1080 / 60 fps / AAC 48 kHz 立体声。
- 混音：`out/audio/mix-final.wav`，−16.0 LUFS、−4.1 dBTP、LRA 6.8；13 个点击音效与 7 个 whoosh 落在实际点击帧。
- 校验：`tools/verify.mjs` 报告无黑帧段、逐帧亮度最大跳变 4.51/255（60.0 s）、两版一致。
- 文档：`docs/check-report.md`、`docs/version-record.md`、`deliverables/README.md`、`deliverables/narration.md`、`deliverables/GameLibrary-promo.srt` 均已定稿。
- 未做：旁白录制、4K、上传与发布。
