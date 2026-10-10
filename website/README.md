# GameLibrary 宣传网站

网站 v1.2.0 参考 [Agent.Space](https://agent.space/) 的深色排版、灰白大标题、章节留白和简洁导航重构。文案改为具体操作说明，保留真实应用 logo 和实机图。[DESIGN.md](DESIGN.md) 记录当前设计约定，[实施记录](../docs/code-review-graph/website.md) 包含源码审计、架构图、文件范围及回滚方法。

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

本次沿用现有 `lib/site.ts` 的下载版本 v1.7.5，保留[该版本](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.5)经维护者明确豁免验收后正式发布的说明：仅完成生产编译，测试、独立检查和人工验收均未执行。Windows 程序未签名，提供 SHA-256 清单；两张截图仍明确标注 v1.5.5。网站重构不更新产品版本或发布状态，四种网页语言也不代表桌面包支持四种界面语言。

## 本次交付 — 2026-10-10

已重排页面、重写四语言正文及搜索摘要、同步主题默认值和响应式图片宽度。TypeScript 类型检查与两项既有主题测试通过；未运行生产构建、静态门禁或浏览器验收。参考站的 HTML 和样式已读取，浏览器画面读取超时。下方记录仅对应历史 v1.1.0。

## 验证 — 2026-10-02

生产构建、TypeScript、两项主题测试、静态门禁及其负例自检通过。门禁覆盖四语言、section ID、项目路径前缀、本地图片和 PNG 尺寸、可访问 dialog 名称、下载链接一致性、canonical/hreflang 与 sitemap。

浏览器实测四语言 `320 / 375 / 768 / 1440px` 无横向溢出，无已加载图片损坏；正式导出页四个路径与素材可用。截图查看器开关、Escape、焦点恢复与滚动锁定、手机导航、键盘语言选择、FAQ、主题切换和刷新持久化、减弱动画均已验证。浏览器无错误日志。

本地证据在 `../artifacts/website-prototype-v1.1.0/`，包含桌面和手机完整截图。它们是本地静态导出预览，不代表网站已部署。此次未重跑 Lighthouse、200% 缩放、无脚本模式或第三方语言审校，不沿用旧版分数。

## 发布

`.github/workflows/website.yml` 沿用原有检查及 Pages 发布流程。推送、启用 Pages、部署与产品 Release 均需明确授权；本次只提交本地网站代码。产品版本仍由 `Directory.Build.props` 管理，网站版本独立为 1.2.0。
