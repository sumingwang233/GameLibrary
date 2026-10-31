# v1.8.0 应用截图与网站内容更新

2026-10-26。网站 v1.5.0 的演示图直接采集自 v1.8.0 桌面程序，替换原来的 v1.5.5 截图。

## 采集来源

| 项目 | 记录 |
|---|---|
| 程序 | 本机最终打包暂存目录 `artifacts/dist/staging/d431c9a5e2374dddaa8367ad86d75fef/GameLibrary/GameLibrary.Desktop.exe`，文件产品版本为 1.8.0 |
| 运行方式 | 启动真实 Tauri / WebView2 程序，连接配套 Host 与桥接进程；未使用宣传片的浏览器 IPC 垫片 |
| 数据隔离 | 仅子进程设置 `GAMELIBRARY_DATA_DIR`、`WEBVIEW2_USER_DATA_FOLDER` 与本机调试端口；不修改系统或用户环境变量 |
| 示例库 | 12 个虚构标题、4 个用户标签、应用默认封面；通过公开 `roots.add`、`games.create`、`tags.create`、`tags.assign` 和 `games.update` 操作建立 |
| 文件入口 | 在本次独立目录中复制系统 `where.exe` 作为样例入口，不运行这些入口，不操作私人游戏 |
| 主题 | 通过真实 `settings.update` 写入 light / dark，重新加载后采集同一库；标签页使用应用原有“管理标签”入口 |
| 画面 | 简体中文，1320×820，截图保存原始 PNG，不重绘控件、不替换界面文字、不对截图做调色 |
| 本地证据 | `artifacts/website-captures/v1.8.0-1793015920869/capture.json`；本次采集脚本保留于 `.tmp/capture-website-v180.mjs`，不纳入发布源码 |

首次尝试在已连接的桌面会话中初始化空库，Host 切换库纪元后旧桥接连接退出，该次未产生截图。最终先用配套 CLI 初始化新的独立库，再启动桌面程序，四张图均来自后一次成功采集。

| 文件 | 内容 |
|---|---|
| `public/assets/library.png` | 浅色游戏库：12 个示例条目、收藏、标签和批量操作栏 |
| `public/assets/library-dark.png` | 同一游戏库的深色模式 |
| `public/assets/tags.png` | 浅色标签管理：分类、关联游戏数、标签星级 |
| `public/assets/tags-dark.png` | 同一标签管理页的深色模式 |

四张图已逐张查看。素材采集仅用于本次演示，不能据此宣称完成了桌面应用功能验收。

## 网页如何使用

浅色图保留 `library.png` / `tags.png` 名称，仓库 README 因而也显示浅色图。原有图片转换脚本使用 Sharp，为四张图分别生成 440、880、1320px WebP；PNG 用于放大查看。源图提交，派生图片沿用已有忽略规则。

`Screenshot` 的缩略图与放大图均包含浅深色素材。CSS 复用主题初始化与选择器已经维护的 `.dark` 类，控制对应图片显示；新访客默认浅色，手动选择、保存偏好及跟随系统继续使用同一份主题逻辑。没有新增主题状态、监听器、路由或依赖。截图不使用滤镜模拟主题。

## 内容依据与改动范围

2026-10-26 使用 GitHub 插件读取 [v1.8.0 Release API](https://api.github.com/repos/sumingwang233/GameLibrary/releases/tags/v1.8.0)，确认 `draft=false`、`prerelease=false`，安装版、便携版、工具包与校验清单均已上传。网页下载统一指向 [v1.8.0](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.8.0)。Release 正文仍含预发布阶段的旧说明，网站的正式版标记依据当前 API 状态。

| 宣传内容 | 源码或发布依据 |
|---|---|
| 批量收藏、标签、移出记录；审核失败项保留选择 | `docs/releases/v1.8.0.md`、`src/GameLibrary.Tauri/src/lib/hooks/`、`contracts/operations.v1.json` |
| 后台服务重启后重新连接，扫描和标题翻译停止无效等待 | `docs/releases/v1.8.0.md` 与对应客户端连接、轮询实现 |
| 徽章、焦点和减少动态效果改进；客户端显式权限 | `docs/releases/v1.8.0.md`；网页不扩大为“整个应用已通过无障碍认证” |
| Godot 识别 | `src/GameLibrary.Domain/Detection/EngineDetectorSet.cs`、`Detectors/AdditionalEngineDetector.cs` |
| 翻译组件可能需要联网下载 | `src/GameLibrary.Host/Hosting/UnityTranslationPayload.cs`；移除无条件的离线安装表述 |

四语言同步新增 `#updates`、批量标签说明、截图版本和替代文本、升级前退出托盘及数据保留说明。合并下载区重复的发布说明入口。主导航首项改为本版更新，原 `#advantages` 章节与锚点仍保留。根目录四语言 README 仅同步当前截图来源。

组件继续由 `MarketingPage` 组织内容、`GlassControls` 承担交互。网站版本在 `package.json` 与 `package-lock.json` 同步为 1.5.0；桌面产品版本不改动。回滚时撤回本次网站提交，并重新生成对应版本的 WebP。

## 执行与待验收

- 已完成：真实应用截图采集、四张原图查看、现有图片转换脚本执行、源码与文案审阅、code-review-graph 更新。
- 中文文案已按 `humanizer` 与 `shuorenhua` 整理，保留版本、条件和功能边界。
- 未运行：测试、构建、lint、类型检查、网站静态门禁与浏览器验收；未推送或部署。

后续获得验证授权时，优先查看四语言的 320 / 375 / 768 / 1440px 页面、浅深主题和跟随系统、缩略图与放大图的一致性、手机导航及长标题换行，再执行现有网站检查。历史版本的通过记录不计入本轮。
