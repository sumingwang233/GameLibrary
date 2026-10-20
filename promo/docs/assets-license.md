# 宣传片配乐与音效许可记录

采集日期：2026-10-20（Asia/Singapore）
范围：150 秒 / 16:9 中文产品宣传片，仅商用宣传用途。
本文件只记录 `promo/` 内的素材；全部素材已用 `ffprobe` 校验为真实音频，SHA-256 为下载/生成后的原样哈希。

## 一、配乐候选

| 文件路径 | 用途 | 来源 URL | 作者 | 许可 | 署名要求 | 时长/格式 | SHA-256 | 获取日期 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `promo/assets/music/incompetech_Pamgaea.mp3` | **推荐主曲**：全片配乐基底 | 下载页 <https://incompetech.com/music/royalty-free/music.html?isrc=USUAN1300036>；直链 <https://incompetech.com/music/royalty-free/mp3-royaltyfree/Pamgaea.mp3> | Kevin MacLeod | CC BY 4.0 · <https://creativecommons.org/licenses/by/4.0/> | 是（片尾或简介必须署名，见下方署名模板） | 168.57 s / MP3 320 kbps 44.1 kHz 立体声 | `9f0ad72567c49d9e1ab895937bef03058a6fc1cfcc20ba7fdd4b681f3c7b5586` | 2026-10-20 |
| `promo/assets/music/incompetech_One_Sly_Move.mp3` | 备选：律动更明确、有内置空拍的版本 | 下载页 <https://incompetech.com/music/royalty-free/music.html?isrc=USUAN1100521>；直链 <https://incompetech.com/music/royalty-free/mp3-royaltyfree/One%20Sly%20Move.mp3> | Kevin MacLeod | CC BY 4.0 · <https://creativecommons.org/licenses/by/4.0/> | 是 | 161.75 s / MP3 320 kbps 44.1 kHz 立体声 | `8df8849343b0f81009acd93c5e055f9f43bf1239092cddc5b50f053fc4fe491b` | 2026-10-20 |
| `promo/assets/music/incompetech_Space_1990-B.mp3` | 备选：最暖、最慢，适合长镜头 | 下载页 <https://incompetech.com/music/royalty-free/music.html?isrc=USUAN1200109>；直链 <https://incompetech.com/music/royalty-free/mp3-royaltyfree/Space%201990-B.mp3> | Kevin MacLeod | CC BY 4.0 · <https://creativecommons.org/licenses/by/4.0/> | 是 | 178.63 s / MP3 320 kbps 48 kHz 立体声 | `ad9ca323958dd52543d19284cc8e3d470fd82c8429b597e3c2de4dbf575a5184` | 2026-10-20 |
| `promo/assets/music/freesound_szegvari_OldCarLoFi.wav.mp3` | CC0 备选：Lo-Fi 旅行质感，需衰减约 9 dB 才不过载 | 下载页 <https://freesound.org/people/szegvari/sounds/595788>；直链 <https://cdn.freesound.org/previews/595/595788_2282212-hq.mp3> | szegvari | CC0 1.0 · <https://creativecommons.org/publicdomain/zero/1.0/> | 否 | 71.94 s / MP3 320 kbps 立体声 | `4cea235397d76ec36fe280bf8970c63ada757ca5d97faa341adfeb8831eef403` | 2026-10-20 |

### 署名模板（选用任一 CC BY 曲目时必填）

> "Pamgaea" by Kevin MacLeod (incompetech.com)
> Licensed under Creative Commons: By Attribution 4.0 License
> <http://creativecommons.org/licenses/by/4.0/>

产品宣传片场景下，建议把这一段放进片尾滚动字幕最后一帧，同时在视频简介/落地页保留。CC BY 4.0 不要求把署名放在画面上，但必须让观众能追溯到作者、曲名、许可名称与许可链接，并注明是否作过剪辑。

### 许可核查方式（不是搜索摘要）

- Incompetech 每首曲目页与站点页脚均标注 `http://creativecommons.org/licenses/by/4.0/`，页面源码中可见该链接；曲目元数据（ISRC、BPM、时长）来自站点自身的 `pieces.json` 目录文件。
- Freesound 条目的 CC0 标注来自 Openverse API 返回的 `license: cc0` / `license_url: https://creativecommons.org/publicdomain/zero/1.0/` 字段，并回溯到 freesound 原始条目页确认作者与曲名。

## 二、音效

全部为程序化合成，无第三方采样，许可为项目自有（无署名义务）。生成脚本 `promo/audio/make_sfx.py`，固定随机种子 `20260101`，重跑得到字节一致的 WAV。

| 文件路径 | 用途 | 来源 URL | 作者 | 许可 | 署名要求 | 时长/格式 | SHA-256 | 获取日期 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `promo/assets/sfx/sfx_click_light.wav` | 轻点击（按钮/卡片） | 本项目合成 `promo/audio/make_sfx.py` | GameLibrary 项目 | 自有版权 | 否 | 0.120 s / WAV 48 kHz 16-bit 立体声，峰值 −6.0 dBFS | `d911aaec434a8bbe62c84b343b4596b761f38ff7f0ef9eb40a0ce5c63b9a329c` | 2026-10-20 |
| `promo/assets/sfx/sfx_confirm_up.wav` | 界面确认（柔和上行两音 A4→D5） | 同上 | GameLibrary 项目 | 自有版权 | 否 | 0.620 s / WAV 48 kHz 16-bit 立体声，峰值 −7.0 dBFS | `cc2ae9b49ca6e962d0e1e5c1ca3d0f4e0cd347d1db1eab6333d361e7f7f32311` | 2026-10-20 |
| `promo/assets/sfx/sfx_whoosh_soft.wav` | 轻微 whoosh / 转场 | 同上 | GameLibrary 项目 | 自有版权 | 否 | 1.050 s / WAV 48 kHz 16-bit 立体声，峰值 −6.0 dBFS | `bdfc4916927f84c1e206fe99e48c8667a83e61fc74cab0e6e80eaca952db857b` | 2026-10-20 |
| `promo/assets/sfx/sfx_scroll_rustle.wav` | 滚动/打字极轻沙沙声 | 同上 | GameLibrary 项目 | 自有版权 | 否 | 1.150 s / WAV 48 kHz 16-bit 立体声，峰值 −9.0 dBFS | `065f7322ac8f1c3ac25cb242eea701ec98905770168b9e0bebe6949bc8e5dd11` | 2026-10-20 |

## 三、排除的候选与原因

| 候选 | 来源 | 排除原因 |
| --- | --- | --- |
| FreePD 全站 | <https://freepd.com/> | 站点已永久关闭（页面原文 "FreePD.com - Site Closed"，服务期 2008–2025），无法取得许可页与直链；网上流传的 FreePD 转存文件回溯不到官方页面。 |
| Pixabay Music 全部曲目 | <https://pixabay.com/music/> 及许可页 <https://pixabay.com/service/license-summary/> | 许可页被 Cloudflare 拦截，返回 HTTP 403 "Just a moment..."，无法取得许可条款原文（任务要求许可页原文可查）。**待人工确认**：若需要用，须人工在浏览器打开并截图存档 Pixabay Content License，确认其允许商业宣传片使用且不要求署名之后才能入库。 |
| `incompetech_Lasting_Hope.mp3`（Kevin MacLeod, CC BY 4.0, 143.93 s） | <https://incompetech.com/music/royalty-free/music.html?isrc=USUAN1100178> | 编曲含 Choir（人声合唱），与"无人声"要求冲突；时长 143.93 s 比 150 s 短 6 s，需要额外补尾。文件已删除，避免误用。 |
| `freesound_Andrewkn_Episode_I.mp3`（Andrewkn, CC0, 156.83 s） | <https://freesound.org/people/Andrewkn/sounds/486447> | 标题 "Episode I" 高度疑似播客/人声节目单集，本环境无法试听，无法确认是器乐；平均 RMS 仅 −33.8 dBFS、峰值 −15.4 dBFS，增益后底噪风险高。文件已删除。 |
| YouTube 音频库、需注册/登录才能下载的曲库、来源不明的 AI 生成曲 | — | 按任务硬性约束一类排除：平台绑定、无法独立留存许可原文、来源不可追溯。 |

## 四、待人工确认项

1. **Pixabay / 任何 Cloudflare 拦截页面**：本环境只能拿到 HTTP 403 的拦截页，许可原文未验证，未入库。
2. **各曲是否真的无人声**：本环境无试听能力，只能依据曲目页的乐器清单（Pamgaea、One Sly Move、Space 1990-B 均为 Synths/EP/鼓/吉他，无 Voice）与频谱特征判断；上线前需人工完整试听一遍确认没有采样人声、念白或广告语。
3. **`OldCarLoFi` 的母带电平**：ffprobe/波形显示峰值 +2.7 dBFS（已削波），入库使用前必须整体衰减 ≥9 dB 并检查削波失真是否可闻。
4. **署名文案落位**：若最终选 CC BY 曲目，需确认片尾字幕与视频简介两处都已放署名，并记录剪辑改动说明。

## 五、实际选用与署名落位（2026-10-20 定稿）

- 选用曲目：`Pamgaea`（Kevin MacLeod，CC BY 4.0）。取第 10–160 秒，片长 150 s；2.5 s 淡入、3.5 s 淡出，整体电平下调并在混音后统一到 −16 LUFS，属剪辑与电平改动。
- 片尾卡（140–150 s）已放一行：`配乐 Pamgaea · Kevin MacLeod · CC BY 4.0`。
- 发布到视频平台时，须在视频简介同时放置 `docs/assets-license.md` 第一节的完整署名模板（含许可链接与剪辑说明）。
- 未使用 One Sly Move、Space 1990-B、OldCarLoFi（保留在 `assets/music/` 作为备选，未进入成片）。

## 六、字体、封面与界面素材

| 素材 | 来源 | 许可 / 使用说明 |
| --- | --- | --- |
| 界面字体 Microsoft YaHei UI（微软雅黑） | Windows 系统自带字体 | 随系统授权使用；**未随工程分发字体文件**。渲染机需为 Windows 且装有该字体，否则会回退到 `sans-serif`，字形会变。 |
| 12 个示例游戏封面 | `promo/ui-host/mock.js` 中按色相/图形/字形程序化生成的 600×900 SVG | 本项目原创，无第三方素材；不含真实游戏名称或美术 |
| 产品界面、图标与 `app-icon.png` | 仓库 `src/GameLibrary.Tauri` 构建产物（MIT License，`LICENSE`） | 与锁定版本一致；未改动产品代码 |
| 片尾卡背景、网格、品牌字标 | `promo/film/endcard.html` 代码绘制 | 本项目原创 |
| 文件夹 / 卡片 / 流程图等概念图形 | `promo/film/film.js` Canvas 绘制 | 本项目原创 |
| 音效 4 个 WAV | `promo/audio/make_sfx.py` 程序化合成（seed 20260101） | 项目自有，无署名义务 |

示例库中的游戏名（Hollow Tide、Neon Drift、Ashen Vale 等）为原创虚构名称，用于演示；不对应真实发行作品。片中出现的路径（`D:\Games\Classics`、`D:\Games\Library`、`D:\Tools\MTool`）为示例路径，不含个人账号或密钥；Unity 设置页的 API Key 字段只显示“已保存，留空保留原密钥”。

