# 封面格式兼容约定

`GameCoverService.PrepareImage` 是导入、选择历史封面及目录同步的统一图片准备入口，返回图片字节和对应扩展名。PNG/JPEG/GIF 经现有 GDI 解码后标准化为 PNG；WebP 是兼容例外：现有 GDI 不支持其像素解码，因此保留原 WebP 字节及 `.webp` 扩展名，不把 WebP 字节写入 `.png` 文件，不新增 decoder 依赖。

WebP 按 [Google WebP 容器规范](https://developers.google.com/speed/webp/docs/riff_container)及[无损图片头规范](https://developers.google.com/speed/webp/docs/webp_lossless_bitstream_specification)检查 RIFF/WEBP 签名、文件与 chunk 长度、padding、VP8X/VP8L/VP8 头和尺寸；动画也检查帧头及帧内图片头。原文件最大 5 MiB，最大边长 8192、总像素 3200 万。该校验不等同于完整压缩流解码，像素内容仍由展示端 WebP decoder 处理。

目录型游戏写入 `cover.png` 或 `cover.webp`；单文件游戏写入 `<游戏文件 stem>.cover.png` 或 `.cover.webp`。自动补齐不覆盖已有封面；显式导入或恢复先将旧格式原文件保存到库内资产历史，再原子写入新封面，成功后清理被替代的目录封面。历史文件保留原扩展名和字节。

测试中的固定 Base64 来自 Pillow 生成的 2×2 单色 VP8L、VP8 和 VP8X 图片，不来自用户封面。新增回归覆盖路径及粘贴导入、格式切换和恢复、同目录多游戏、尺寸与 5 MiB 边界；本轮 dotnet 构建及测试由父代理统一执行。
