# 交付说明

## 视频

| 文件 | 说明 |
| --- | --- |
| `out/film/GameLibrary-promo-1080p60.mp4` | 主交付：1920×1080、60 fps、H.264 + AAC、150 s、**字幕烧录** |
| `out/film/GameLibrary-promo-1080p60-nosub.mp4` | 无字幕母版（同一画面与音轨，仅去掉底部字幕与压暗层） |

音频：音乐 Pamgaea（Kevin MacLeod，CC BY 4.0）第 10 秒起 150 秒，2.5 s 淡入 / 3.5 s 淡出；13 个点击音效与 7 个转场轻 whoosh 落在真实点击帧上。目标 −16 LUFS，成片实测 −16.0 LUFS。

## 文本

| 文件 | 说明 |
| --- | --- |
| `GameLibrary-promo.srt` | 独立字幕，20 条，与成片时间一致 |
| `narration.md` | 完整旁白稿、读法提示、补录流程；**第一版未录制旁白** |

## 工程与文档（`promo/`）

- `README.md`：环境、运行方式、全部渲染命令。
- `config/film.json`：片长、分辨率、帧率、版本、色板、字号、音频目标。
- `config/copy.json`：字幕、屏幕注释、12 个示例游戏与标签。
- `config/shots.json`：44 个镜头的时间、素材板、相机、光标与注释（唯一时间轴真源）。
- `docs/storyboard.md`：分镜时间表与字幕全表（由镜头表生成）。
- `docs/feature-evidence.md`：功能证据表（宣传表述 → 界面/代码 → 前置条件 → 展示方式）。
- `docs/version-record.md`：版本、提交、发行状态与画面标注规则。
- `docs/assets-license.md`：音乐、音效的来源、许可与署名要求，以及被排除的候选。
- `docs/check-report.md`：已验证 / 未验证 / 示意镜头与原因。
- `docs/ipc-contract.md`：为拍片搭建的浏览器侧 IPC 垫片所依据的前端契约。
- `out/stills/`：k1–k5 关键静帧；`deliverables/contact-sheet.png`：整片缩略图表。

## 署名（CC BY 4.0）

> "Pamgaea" by Kevin MacLeod (incompetech.com)
> Licensed under Creative Commons: By Attribution 4.0 License
> <http://creativecommons.org/licenses/by/4.0/>
> 本片对原曲做了剪辑：取第 10–160 秒，并做了淡入淡出与整体电平调整。

片尾卡已放一行「配乐 Pamgaea · Kevin MacLeod · CC BY 4.0」。发布到视频平台时，请把上面四行放进视频简介，并保留片尾卡。

## 没有做的事

- 没有上传、发布、修改仓库主页，也没有导出 4K。
- 没有录制旁白，没有用静音文件冒充配音。
- 没有演示游戏内翻译效果，没有伪造启动成功的系统提示。


