---
version: alpha
name: GameLibrary Marketing — Paper and Pixels
description: 暖纸收藏夹收纳像素世界，以原版应用截图展示本地游戏的扫描、审核、整理与启动。
colors:
  primary: "#BE4B32"
  on-primary: "#FFFDF8"
  background: "#F7F2E7"
  surface: "#FFFCF4"
  on-surface: "#202B3C"
  muted: "#64675E"
  outline: "#DCD0BB"
  primary-dark: "#EFAB87"
  on-primary-dark: "#172329"
  background-dark: "#1C242B"
  surface-dark: "#2B353B"
  on-surface-dark: "#F0EADF"
  muted-dark: "#B5B7AE"
  outline-dark: "#43505A"
typography:
  h1:
    fontFamily: "Palatino Linotype, STSong, 华文宋体, Songti SC, SimSun, Georgia, serif"
    fontSize: 68px
    fontWeight: 700
    lineHeight: 1.22
    letterSpacing: -0.045em
  h1-mobile:
    fontFamily: "Palatino Linotype, STSong, 华文宋体, Songti SC, SimSun, Georgia, serif"
    fontSize: 52px
    fontWeight: 700
    lineHeight: 1.25
    letterSpacing: -0.045em
  h2:
    fontFamily: "Palatino Linotype, STSong, 华文宋体, Songti SC, SimSun, Georgia, serif"
    fontSize: 48px
    fontWeight: 700
    lineHeight: 1.32
    letterSpacing: -0.035em
  body:
    fontFamily: "Segoe UI, Microsoft YaHei, sans-serif"
    fontSize: 16px
    fontWeight: 400
    lineHeight: 1.8
rounded:
  button: 4px
  menu: 4px
  figure: 8px
spacing:
  sm: 8px
  md: 16px
  lg: 24px
  xl: 48px
  xxl: 68px
  section: 112px
---

# GameLibrary 宣传网页设计规范

网站 v1.4.0，2026-10-21。用户确认采用“纸雕为底、像素点睛”：以第二张复古像素海报和第五张纸雕收藏夹海报为视觉来源，在现有静态站内重排布局与样式。它替代 v1.3.0 的灰白视觉；历史设计与交付记录保留在 [网站实施记录](../docs/code-review-graph/website.md)。

## 视觉来源与融合方法

- [纸雕收藏夹](../promo/posters/2026-10-20/01-editorial-library.png)提供暖纸底色、宋体大标题、纸张厚度和收藏主题。
- [复古像素修正版](../promo/posters/2026-10-20/02-pixel-scan-review-launch-v2.png)提供像素风景、旅行者、星形节点和扫描到启动的路径。
- 首屏新插画把像素风景放进纸雕收藏夹，避免直接把两张带字海报贴进页面。流程插画沿用同组风景与角色；角色完整露出，没有鼠标遮挡。
- 官方平面 logo 保持原样。装饰插画不代表软件界面或游戏内翻译效果；应用截图继续使用仓库原版 PNG。

新插画由内置 `image_gen` 生成，使用真实透明背景。原图、尺寸、参考输入与完整提示词见 [素材生成记录](design/2026-10-21-art-prompts.md)。

## 色彩、文字与布局

令牌定义在 `app/globals.css`。奶油纸色 `--background`、深蓝墨色 `--ink`、朱橙 `--primary` 和苔绿 `--secondary` 贯穿全页；明暗主题使用同一组视觉语汇。页面背景只有轻微静态纸点纹理，不使用持续动画。

`h1/h2` 使用本机宋体或衬线字体，正文仍为本机无衬线字体。英文、繁中和日文使用对应字体回退。像素元素用于插画、编号和小型装饰，不把中文正文处理成难读的像素字。

容器最大 1280px，桌面左右内距 48px，首屏约 0.9:1.1 两列。标题和主要下载入口在左，透明插画在右。按钮为实体朱橙、小圆角和轻压印；纸签、截图底座和下载文件夹形成不同层次，普通功能说明仍用文本与分隔线。

新访客默认浅色。已保存的 Light / Dark / System 偏好、原存储键、跨标签同步和系统主题监听继续使用；三套布局、预绘制脚本、选择器和 `theme-color` 同步。

## 页面内容与真实证据

| 区域 | 展示 |
|---|---|
| 首屏 | 纸雕收藏夹与像素封面；Windows、稳定下载版本、安装入口、真实界面锚点、无需账号与离线管理 |
| `#experience` | 目录、书架、手柄入口的像素路径；HTML 三步“扫描指定目录 → 确认游戏入口 → 从游戏库启动” |
| `#app-preview` | 完整游戏库实机图，标明 v1.5.5、简体中文和隔离示例库，点击放大；下方列识别的引擎 |
| `#advantages` | 本机数据、原文件保留、审核入库、引擎识别，使用编号和分隔说明 |
| `#collection` | 标签与收藏说明、原版标签截图、作为例子的纸签 |
| `#translation` | Unity 翻译插件与字体配置、备份恢复；注明自配服务与因游戏而异的支持和效果 |
| `#launch` / `#safety` | 启动文件、参数、SWF 前提、本机数据与文件操作边界 |
| `#download` | 文件夹面板里的安装版、便携版、工具包、SHA-256 和发布说明 |
| `#questions` / 页脚 | 六项 FAQ、真实项目链接、四语言入口 |

下载源继续读取 `lib/site.ts` 的公开稳定版 v1.7.13；网站版本与桌面产品版本独立。截图中的旧图标、封面和控件不重绘成新版。宣传文案不承诺全程离线、所有游戏汉化或已验证的翻译效果。

## 文件范围、路由与状态

- `components/MarketingPage.tsx`：重排既有章节，加入首屏和流程插画、收藏纸签、下载面板；四语言正文仍共用 `translate()`。
- `app/globals.css`：纸张/像素视觉令牌、布局、主题、原生控件和响应式样式。
- `components/GlassControls.tsx`：保留所有交互；默认主题同步浅色；截图 `wide` 与加载优先级 `priority` 分开。
- `lib/theme.ts`、三组 `layout.tsx`、现有 `scripts/theme.test.mjs`：同步浅色缺省和两套主题色。
- `public/assets/hero-paper-pixel.png`、`pixel-journey.png`、`scripts/optimize-images.mjs`：透明原图及 640/960/1440px WebP；原有实机图仍用 440/880/1320px 预览。
- `package.json` / `package-lock.json`：网站独立版本更新到 1.4.0，不新增依赖。

路由仍为简中 `/`、繁中 `/zh-TW/`、英文 `/en/`、日文 `/ja/`，生产前缀仍为 `/GameLibrary`。页面保留 Server Component，浏览器状态仍集中在 `GlassControls`；没有增加状态库、客户端外壳或远程字体。

## 交互与响应式

语言和主题菜单继续使用原生 `details`，手机导航和截图使用原生 `dialog`，保留 Escape、焦点恢复、菜单互斥、点击外部关闭与弹窗滚动锁定。FAQ、跳至正文、可见键盘焦点和 44px 操作区继续保留。缩略截图使用 WebP，放大仍读取原始 PNG。

1180px 以下导航收紧；920px 以下首屏与内容单列；640px 以下流程说明纵排、按钮与下载面板适配手机；360px 以下继续收敛内距和字级。长文件名允许折行，主题不会给真实截图加滤镜。减弱动画、高对比和系统强制配色都有对应规则。

## 迁移、回滚与验收

迁移顺序：现状与图谱审计 → 确认融合方案 → 独立生成无文字插画 → 页面/样式/主题并行修改 → 生成响应式图片 → 同步图谱与文档 → 本地提交。P0 保留真实内容、路由与交互；P1 落地统一的纸雕/像素视觉；P2 处理多语言排版与响应式细节。

回滚使用本次网站提交的 `git revert`，同时撤销页面、样式、主题、图片管线和独立网站版本。已有游戏库数据与桌面产品不在本次改动范围。回退后重新生成旧版响应式图片即可。

验收标准：四语言章节与锚点完整；下载仍对应稳定版；两主题及保存偏好一致；原版截图可放大；手机无横向溢出；键盘、Escape 和减弱动画行为正常；装饰图与实机图的性质清楚。

初次实现已进行源码审计、素材生成和 WebP 转换，当时没有运行测试、构建、lint、类型检查、静态门禁或浏览器验收。源码与插画检查不代表设备验收通过；后续获授权的 GitHub 上传、发布检查与线上结果见[发布记录](../docs/deployment/website-v1.4.0.md)。
