---
version: alpha
name: GameLibrary Marketing — Local Library
description: 参考 Agent.Space 的灰白层级、居中大标题和章节留白，以真实应用界面说明本地游戏的扫描、整理与启动。
colors:
  primary: "#202123"
  on-primary: "#FFFFFF"
  background: "#F5F5F4"
  surface: "#FFFFFF"
  on-surface: "#202123"
  muted: "#62646B"
  outline: "#D7D7D9"
  primary-dark: "#F1F1F2"
  on-primary-dark: "#111214"
  background-dark: "#111214"
  surface-dark: "#1B1C20"
  on-surface-dark: "#F1F1F2"
  muted-dark: "#A6AAB3"
  outline-dark: "#303237"
typography:
  h1:
    fontFamily: "Segoe UI, Microsoft YaHei, sans-serif"
    fontSize: 76px
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: -0.035em
  h1-mobile:
    fontFamily: "Segoe UI, Microsoft YaHei, sans-serif"
    fontSize: 34px
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: -0.03em
  h2:
    fontFamily: "Segoe UI, Microsoft YaHei, sans-serif"
    fontSize: 44px
    fontWeight: 600
    lineHeight: 1.35
    letterSpacing: -0.03em
  body:
    fontFamily: "Segoe UI, Microsoft YaHei, sans-serif"
    fontSize: 16px
    fontWeight: 400
    lineHeight: 1.75
    letterSpacing: 0em
rounded:
  button: 999px
  menu: 12px
  figure: 14px
spacing:
  sm: 8px
  md: 16px
  lg: 24px
  xl: 32px
  xxl: 48px
  section: 112px
  section-mobile: 72px
---

# GameLibrary 宣传网页设计规范

适用网站 v1.2.0，2026-10-10。本次用户将参考改为 [Agent.Space](https://agent.space/)，替代 v1.1.0 的 Stitch 原型排版；原型保留在 `FrontEnd_Design`，作为历史设计输入。架构、迁移、回滚和执行记录见[网站实施记录](../docs/code-review-graph/website.md)。

## 视觉与排版

以近黑背景、灰白文字、居中大标题和完整实机图为主。参考站的首页 HTML 和实际 CSS 已读取；浏览器截图接口超时，未完成参考站画面比对。只参考排版、色阶、按钮与留白，不复用其品牌素材或动画代码。

- 新访客默认深色；保存过的 Light / Dark / System 偏好继续有效。三个入口的初始 HTML、绘制前脚本和主题选择器使用同一规则。
- 页面容器最大 1200px，首屏截图最大 1080px。桌面两级标题为灰色前句、亮色后句，按钮在下方，完整实机图继续向下展开。
- 字体使用本机系统字体。繁中使用 Microsoft JhengHei，日文使用 Yu Gothic；不加载远程字体。
- 章节纵向间隔 112px，手机为 72px。大标题使用 clamp；正文 16px，手机说明 15px。
- 功能说明以分隔线和文本列表呈现。截图、菜单和原生弹窗保留边界，避免每个功能都套一张卡片。
- 使用原有彩色平面 logo，保持图片比例与色彩。网站控件以中性色为主，色彩主要来自 logo 和实机图。
- 页面主体不使用滚动接管、Canvas 场景或持续动画。菜单和弹窗只有短过渡；减弱动画偏好下取消动画及平滑滚动。

CSS 变量由 `app/globals.css` 定义。上方 tokens 记录对应设计值；`version: alpha` 是文档格式版本，与网站或桌面产品版本无关。

## 内容顺序

| 区域 | 内容 |
|---|---|
| 首屏 | “游戏放在各处，在这里一起管理。”；Windows / x64 / 当前下载版本；安装按钮、实机图入口、无需账号和 MIT |
| 实机与引擎 | 完整游戏库截图，标明实际版本、界面语言和示例库；列出识别的引擎名称 |
| 扫描 `#experience` | 添加目录、扫描候选、逐项确认；说明只扫描指定位置 |
| 整理 `#collection` | 标题、路径与标签搜索；真实标签管理截图；收藏和标签星级排序 |
| 启动 `#launch` | 保存启动文件与参数；手动添加 EXE / LNK；SWF 使用已有 Windows 关联播放器 |
| 数据 `#safety` | 本机保存、离线管理；常规移除记录与回收文件分开；说明更新检查和辅助程序联网边界 |
| 下载 `#download` | 安装版为主要入口，便携版、工具包和 SHA-256 清单为次级入口；保留当前发布的验收豁免信息 |
| FAQ `#questions` | 六项具体问题，使用原生 details 展开 |
| 页脚 | 项目、反馈、许可证与四语言入口 |

文案先说明具体操作和结果，不写“继续冒险”“下一次重逢”“你的收藏由你掌握”等无额外信息的口号。不编造用户数、评分、推荐语或功能。四语言页面使用相同内容顺序、下载版本和素材；网站语言不等于桌面软件语言。

## 素材与下载

| 素材 | 来源与用途 |
|---|---|
| `public/assets/icon.png` | 已认可的 GameLibrary 应用 logo；页头、安装区和页脚 |
| `public/assets/library.png` | v1.5.5 简体中文隔离示例库的完整游戏库实机图 |
| `public/assets/tags.png` | v1.5.5 简体中文隔离示例库的标签管理实机图 |
| `*-440/880/1320.webp` | 既有优化脚本从真实 PNG 生成的响应式预览，点击后查看原始 PNG |

图片版本依据[主 README](../README.md)。不修改截图内的旧图标或控件冒充新版本，不生成伪实机图。缺少扫描审核和启动实机素材时，用操作说明代替示意窗口。

下载版本沿用本次开始时 `lib/site.ts` 中的 v1.7.5，不因网站重构变更产品发布。保留 Setup、Portable-win-x64、Tools-win-x64 和版本化 SHA256SUMS 名称，以及该版本已公开但未验收、Windows 程序未签名的现有说明。发布与部署遵循[发布原则](../docs/release-policy.md)。

## 交互、响应式与边界

复用 `Header`、`Screenshot` 和 `LocalTooltip`；不新增组件库、路由、全局状态或依赖。语言与主题菜单为原生 details，手机导航和截图查看器为原生 dialog，保留 Escape、焦点恢复和弹窗滚动锁定。FAQ 原生支持键盘展开，链接及按钮保留可见焦点与至少 44px 操作区。

900px 以下内容改为单列，640px 以下下载区也改为单列；窄屏按钮允许换行，长文件名允许折行。截图 sizes 随新的首屏和分类区宽度同步调整。真实截图不会因主题变化而改色。

网站保留 `/`、`/zh-TW/`、`/en/`、`/ja/` 四个路径和现有锚点，正式静态路径仍带 `/GameLibrary` 前缀；保留 canonical、hreflang 和 sitemap。主内容仍为服务端组件，客户端仅处理既有交互。

## 交付与验收

P0 是素材、下载和发布状态准确；P1 是版式与四语言文案；P2 是响应式、主题和原生交互。实际检查结果见实施记录，历史 v1.1.0 的验证不能证明本次改动通过。没有部署授权时只提交本地网站代码，不发布产品。
