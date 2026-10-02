# GameLibrary 宣传网站

网站 v1.1.0 按 `FrontEnd_Design/gamelibrary_1`、`gamelibrary_2` 的原型重构；[DESIGN.md](DESIGN.md) 保留产品事实与设计约定。[实施及验收记录](../docs/code-review-graph/website.md) 包含源码审计、架构图、文件清单、迁移与回滚方法。

网站使用现有 Next.js 16 / React 19 / Tailwind 4，导出为静态文件，不需要 Node.js 服务。支持简体中文 `/`、繁體中文 `/zh-TW/`、English `/en/` 和日本語 `/ja/`；正式路径统一带 `/GameLibrary` 前缀。

## 检查与预览

从仓库根目录运行，使用 Node.js 24 和可用的 Python 3：

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

- 浅蓝页面、青绿强调色、不透明表面，保持原型的非对称首屏和区块顺序。
- 新访客默认浅色；已有浅色、深色或跟随系统偏好继续保留。使用原有 localStorage 键，首次绘制与选择器共享逻辑。
- `GlassControls.tsx` 与 `glass()` 保留现有文件和调用名；实际样式已改为 `surface`，不使用玻璃模糊或背景光球。
- 语言/主题菜单使用原生 `details`；截图与手机导航复用原生 `dialog`。支持 Escape、焦点恢复、滚动锁定；FAQ 支持键盘展开。
- 使用本机字体，不加载远程字体、分析脚本或图片。设置 `prefers-reduced-motion` 时取消移动与平滑滚动。

## 真实素材与版本

页头、下载区和页脚使用 `public/assets/icon.png`，它来自已认可的 GameLibrary 应用 logo。用户确认“真实游戏图标”指应用 logo，不额外引入具体游戏封面。

`library.png` 和 `tags.png` 是仓库已有的真实应用截图：v1.5.5、简体中文、隔离示例库、默认封面。来源依据是[主 README 的截图说明](../README.md)。网页逐图说明版本、界面语言和示例数据性质；截图保留原来的标题栏图标，不修改图片冒充新版软件。点击查看原始 1320×820 PNG，图片加载失败显示明确说明。

扫描区是明确标注的流程示意。原型只要求游戏库和标签管理两处图片，这两处已换为实机图；缺少对应版本的审核/启动截图时，使用准确的文字说明。

主下载与首屏能力对应公开稳定版 v1.5.5；v1.7.3 单独标为“预发布 · 待验收”。稳定版链接来自[公开 Release](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.5.5)。Windows 程序未签名，下载区提供 SHA-256 清单。四种网页语言不代表稳定版桌面包支持四种界面语言。

## 验证 — 2026-10-02

生产构建、TypeScript、两项主题测试、静态门禁及其负例自检通过。门禁覆盖四语言、section ID、项目路径前缀、本地图片和 PNG 尺寸、可访问 dialog 名称、下载链接一致性、canonical/hreflang 与 sitemap。

浏览器实测四语言 `320 / 375 / 768 / 1440px` 无横向溢出，无已加载图片损坏；正式导出页四个路径与素材可用。截图查看器开关、Escape、焦点恢复与滚动锁定、手机导航、键盘语言选择、FAQ、主题切换和刷新持久化、减弱动画均已验证。浏览器无错误日志。

本地证据在 `../artifacts/website-prototype-v1.1.0/`，包含桌面和手机完整截图。它们是本地静态导出预览，不代表网站已部署。此次未重跑 Lighthouse、200% 缩放、无脚本模式或第三方语言审校，不沿用旧版分数。

## 发布

`.github/workflows/website.yml` 沿用原有检查及 Pages 发布流程。推送、启用 Pages、部署与产品 Release 均需明确授权；本次只提交本地网站代码。产品版本仍由 `Directory.Build.props` 管理，网站版本独立为 1.1.0。
