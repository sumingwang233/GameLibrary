# GameLibrary 宣传网站

网站 v1.3.0 采用用户指定设计项目 `333cacd6-dd27-42fb-86aa-087d19e0d3ec` 的最新版 `gamelibrary-marketing.html`。同步容器留白、灰白标题、两列优势列表、章节底色、截图框及手机排版，保留真实应用 logo、实机图和四语言交互。[DESIGN.md](DESIGN.md) 记录当前设计约定，[实施记录](../docs/code-review-graph/website.md) 包含来源指纹、架构图、文件范围及回滚方法。

网站使用现有 Next.js 16 / React 19 / Tailwind 4，导出为静态文件，不需要 Node.js 服务。支持简体中文 `/`、繁體中文 `/zh-TW/`、English `/en/` 和日本語 `/ja/`；正式路径统一带 `/GameLibrary` 前缀。

## 检查与预览

以下命令供明确要求检查或预览时使用；不表示本次均已执行。从仓库根目录运行，使用 Node.js 24 和可用的 Python 3：

```powershell
npm --prefix website ci
npm --prefix website run build
npm --prefix website run typecheck
npm --prefix website test
python scripts/check_website.py --self-test
python scripts/check_website.py
npm --prefix website run dev
```

开发地址为 `http://127.0.0.1:4173/`。若 Windows 的 `python` 指向无可用解释器的应用执行别名，请使用已安装 Python 的明确路径，不修改系统 PATH。构建前用已有的 Sharp 从真实 PNG 生成 440、880、1320px WebP 预览；原图、尺寸和比例保持不变，生成预览与构建文件不提交。

## 外观与交互

- 灰白层级、居中首屏、下方完整实机图；功能区改为标题与分隔列表，下载区保留主要安装入口。
- 新访客默认深色；已有浅色、深色或跟随系统偏好继续保留。使用原有 localStorage 键，首次绘制与选择器共享逻辑。
- `GlassControls.tsx` 与 `glass()` 保留现有文件和调用名；实际样式已改为 `surface`，不使用玻璃模糊或背景光球。
- 语言/主题菜单使用原生 `details`；截图与手机导航复用原生 `dialog`。支持 Escape、焦点恢复、滚动锁定；FAQ 支持键盘展开。
- 使用本机字体，不加载远程字体、分析脚本或图片。设置 `prefers-reduced-motion` 时取消移动与平滑滚动。

## 真实素材与版本

页头、下载区和页脚使用 `public/assets/icon.png`，它来自已认可的 GameLibrary 应用 logo。用户确认“真实游戏图标”指应用 logo，不额外引入具体游戏封面。

`library.png` 和 `tags.png` 是仓库已有的真实应用截图：v1.5.5、简体中文、隔离示例库、默认封面。来源依据是[主 README 的截图说明](../README.md)。网页逐图说明版本、界面语言和示例数据性质；截图保留原来的标题栏图标，不修改图片冒充新版软件。点击查看原始 1320×820 PNG，图片加载失败显示明确说明。

扫描区使用三步操作说明；游戏库和标签管理使用真实截图。缺少对应版本的审核/启动截图时，用准确文字说明，不生成示意软件窗口。

2026-10-13 将 `lib/site.ts` 下载版本同步到 [v1.7.13](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.13)，四语言下载文案登记维护者已确认真机验收并授权正式发布。针对性回归与生产编译已有结果，完整自动门禁未补跑，详见[发行记录](../docs/releases/v1.7.13-validation.md)。Windows 程序仍未签名，提供 SHA-256 清单；两张截图继续标注真实版本 v1.5.5。本次仅同步网站源码，未重新构建或部署 Pages；四种网页语言不代表桌面包支持四种界面语言。

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

`.github/workflows/website.yml` 沿用原有检查及 Pages 发布流程；普通推送只运行检查，在线部署使用 `workflow_dispatch` 的 `publish=true`。本次用户要求将指定设计作为最新网页上传 GitHub，据此同步源码并更新 Pages。产品 Release 不在本次范围，产品版本仍由 `Directory.Build.props` 管理，网站版本独立为 1.3.0。
