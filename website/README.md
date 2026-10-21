# GameLibrary 宣传网站

网站 v1.4.0 采用用户确认的“纸雕为底、像素点睛”：首屏纸雕收藏夹收纳像素风景，流程区用同组像素插画串起扫描、确认、启动。暖纸色、宋体标题、朱橙按钮、纸签和下载文件夹延续两张海报的风格。[DESIGN.md](DESIGN.md) 记录布局、改动范围、迁移与回滚；[网站实施记录](../docs/code-review-graph/website.md) 记录架构与图谱影响。

沿用 Next.js 16 / React 19 / Tailwind 4 静态导出，无新增依赖。简中 `/`、繁中 `/zh-TW/`、英文 `/en/`、日文 `/ja/` 继续共用页面；生产路径前缀仍为 `/GameLibrary`。

## 页面与交互

- 左文右图首屏 → 三步像素路径 → 完整实机图 → 本地优势、标签收藏、Unity 翻译、启动与数据 → 文件夹下载区 → FAQ。
- 新访客默认浅色，保存过的浅色、深色和跟随系统偏好继续生效。三套布局、预绘制脚本和选择器共用原主题键与逻辑。
- 菜单仍为原生 `details`，手机导航与截图查看器仍为原生 `dialog`。保留键盘、Escape、菜单互斥、焦点恢复和弹窗滚动锁定。
- 两张无文字透明插画与真实截图分开展示。手机布局、长文件名折行、减弱动画、高对比和强制配色规则已在源码中配置。
- `GlassControls.tsx` 的截图 `wide` 决定显示宽度，`priority` 只决定加载优先级。首屏插画优先加载，后续插画与截图延后加载。

## 素材与版本

`public/assets/icon.png` 仍为官方平面 logo。`library.png` 和 `tags.png` 保留原版内容：v1.5.5、简体中文、隔离示例库、默认封面；点击放大读取 1320×820 PNG，不重绘成新版界面。

`hero-paper-pixel.png` 与 `pixel-journey.png` 为内置 `image_gen` 生成的透明概念插画。图中封面不是具体游戏资产，页面不以插画冒充实机。完整输入与提示词见 [素材生成记录](design/2026-10-21-art-prompts.md)。

图片管线使用已有 Sharp：截图生成 440/880/1320px WebP，插画生成 640/960/1440px WebP。PNG 源文件提交，派生 WebP 和构建文件按现有忽略规则保留在本机。

下载版本读取 `lib/site.ts` 的正式版 v1.7.22，提供安装版、便携版、工具包和 SHA-256。维护者已明确豁免本次未完成的验收并授权正式发布；未执行项见桌面发行记录。网站版本与桌面产品版本独立。

## v1.4.0 上线

2026-10-21，维护者确认本地预览视觉，并授权上传 GitHub 与部署 Pages。本次从远端 main 建立独立发布检出，仅迁入网站和两张参考海报，保留未发布的桌面程序改动。发布前将 Next.js 修补到 16.3.8，并更新 source-map-js 到 1.2.2。

最终检查、部署工作流与线上核对结果见[发布记录](../docs/deployment/website-v1.4.0.md)。线上地址为 [GameLibrary](https://sumingwang233.github.io/GameLibrary/)。

## 初次本地实现记录

2026-10-21：完成网页源码、主题一致性、两张插画、WebP 转换及图谱/设计文档同步，按仓库约定做本地提交。当时未推送或部署；后续上线记录见上方链接。

初次实现遵守用户的验证策略，没有运行测试、构建、lint、类型检查、静态门禁或浏览器验收。现有主题用例只同步了新默认值与颜色预期；发布阶段另行记录检查结果。多尺寸浏览器验收仍按 [DESIGN.md](DESIGN.md) 的标准执行，不能沿用下方历史结果。

明确要求预览或检查时，可在仓库根目录使用以下命令；此处列出不代表已执行：

```powershell
npm --prefix website run dev
npm --prefix website run typecheck
npm --prefix website test
npm --prefix website run build
python scripts/check_website.py --self-test
python scripts/check_website.py
```

开发地址为 `http://127.0.0.1:4173/`。开发/构建前由现有 predev/prebuild 自动生成 WebP，无需修改系统环境变量。

## 最新设计迁入 — 2026-10-13 · v1.3.0

来源：`C:\Users\sumingwang\AppData\Roaming\Open Design\namespaces\release-stable-win\data\projects\333cacd6-dd27-42fb-86aa-087d19e0d3ec\gamelibrary-marketing.html`，SHA-256 为 `bf6ef2fd669aa2ecf324ea29b4ceb065c337b0a9ea8ace2654ac96e182dd1e3c`。设计中的七项图片资源与现有网站素材逐字节一致，保留原始 PNG 放大查看。中文文案与设计同步，其余三语言保持相同内容，继续使用真实语言路由、主题持久化和原生弹窗，不照搬原型占位链接。

本地生产构建、类型检查、既有两项主题测试、静态门禁及负例自检通过；图谱更新无解析错误。网站提交 `8e91c79` 已推送，[Pages 发布流程](https://github.com/sumingwang233/GameLibrary/actions/runs/38357240004)检查与部署成功。[在线网站](https://sumingwang233.github.io/GameLibrary/)四语言页面、样式及图片均返回 HTTP 200，线上样式与本地生产构建逐字节一致。未执行浏览器视觉与响应式验收。

## 历史内容同步 — 2026-10-13 · v1.2.0

按用户确认的原型 `gamelibrary-marketing.html` 增补宣传内容，同步入仓库 Next.js 站点：首屏再加「独特优势 `#advantages`」与「中文翻译 `#translation`」两节（均四语言，含 `aria-labelledby`）；`nav` 增加「独特优势」锚点。文案更新：首屏 facts 移除「MIT 开源」链接、改为「离线可用 / Works offline」，保留一处本机数据 `LocalTooltip`；FAQ「需要账号或联网吗」补充 Unity 翻译需自填本机密钥；整理区新增「标签归类更顺手」，启动区 SWF 条目补充「整目录入库时生成建议启动方式」，数据区补充默认目录 `%LOCALAPPDATA%\GameLibrary`；下载区移除「关于此版本」段落、仅保留发布说明链接。同步 `globals.css` 网格与样式、`DESIGN.md` 内容顺序，以及本目录图谱记录。

验证结果：`npm --prefix website run typecheck`、`npm --prefix website test`（既有 2 项主题测试）、`npm --prefix website run build`（Next.js 16.3.7 成功导出四语言）、`python scripts/check_website.py --self-test` 与 `python scripts/check_website.py` 均通过；导出 `index.html` 已含 `id="advantages"`、`id="translation"` 及新增文案。浏览器响应式与视觉验收未执行，不沿用历史结果。本次仅同步网站源码，未部署 Pages。

## 本次交付 — 2026-10-10

已重排页面、重写四语言正文及搜索摘要、同步主题默认值和响应式图片宽度。TypeScript 类型检查与两项既有主题测试通过。后续经用户明确授权，已完成本地生产构建、GitHub 同步及 [Pages 发布](https://github.com/sumingwang233/GameLibrary/actions/runs/38014369936)；现有 CI 的构建、类型、主题测试、静态门禁及负例自检均通过。

[线上官网](https://sumingwang233.github.io/GameLibrary/)四语言页面和新样式已核对，本地生产预览运行于 `http://127.0.0.1:4174/GameLibrary/`。本次未完成浏览器响应式和视觉验收，不沿用下方历史结果。参考站的 HTML 和样式已读取，其浏览器画面读取曾超时。

## 验证 — 2026-10-02

生产构建、TypeScript、两项主题测试、静态门禁及其负例自检通过。门禁覆盖四语言、section ID、项目路径前缀、本地图片和 PNG 尺寸、可访问 dialog 名称、下载链接一致性、canonical/hreflang 与 sitemap。

浏览器实测四语言 `320 / 375 / 768 / 1440px` 无横向溢出，无已加载图片损坏；正式导出页四个路径与素材可用。截图查看器开关、Escape、焦点恢复与滚动锁定、手机导航、键盘语言选择、FAQ、主题切换和刷新持久化、减弱动画均已验证。浏览器无错误日志。

本地证据在 `../artifacts/website-prototype-v1.1.0/`，包含桌面和手机完整截图。它们是本地静态导出预览，不代表网站已部署。此次未重跑 Lighthouse、200% 缩放、无脚本模式或第三方语言审校，不沿用旧版分数。

## 发布

`.github/workflows/website.yml` 沿用现有检查与 Pages 发布流程。推送、上传和 `workflow_dispatch publish=true` 部署需要用户单独授权；网站版本独立于桌面产品。以上历史发布结果只适用于各自标明的版本，不代表 v1.4.0 已发布。
