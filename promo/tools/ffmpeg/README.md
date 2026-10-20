# 项目内 FFmpeg

`promo/` 使用的音视频工具已复制到本目录，渲染与混音命令都走这里的二进制，不要求系统预装 FFmpeg。

| 项 | 值 |
|---|---|
| 版本 | `ffmpeg 7.1.1-essentials_build-www.gyan.dev`（2025） |
| 构建 | gyan.dev essentials build，`--enable-gpl --enable-version3`，含 libx264 / libx265 / libass / libfreetype |
| 许可 | GPLv3（构建含 GPL 组件）；本工程只调用其命令行，不链接、不再分发到产品仓库的发布物 |
| 来源 | <https://www.gyan.dev/ffmpeg/builds/>（`ffmpeg-release-essentials.zip`） |
| 获取日期 | 2026-10-20 |
| `ffmpeg.exe` SHA-256 | `B90225987BDD042CCA09A1EFB5E34E9848F2D1DBF5FBCD388753A44145522997` |
| `ffprobe.exe` SHA-256 | `05E8FA639450F8191635192871AE37A3EC3E4638FA12F3B7D49C6522BA16A8ED` |

二进制本体已写入 `promo/.gitignore`，不随仓库提交；需要复现时按上表下载同版本，或运行 `node render/check-ffmpeg.mjs` 让脚本报告缺失并提示下载地址。
