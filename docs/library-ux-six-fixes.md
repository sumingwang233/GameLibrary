# 游戏库体验修复审计（v1.4.4 后续）

## 现状、证据与根因

基线：`efad5dc`（v1.4.4）。沿用 C# Host / SQLite / Tauri / React；无数据库迁移，不更换状态管理。

1. `DirectoryWalker.WalkCore` 只执行调用方规则，空规则集会枚举 `$RECYCLE.BIN`。旧候选也会留在数据库。
2. `CatalogingHandler.AssetsImport` 仅复制到应用 assets；候选入库没有读取游戏目录 cover 的步骤。
3. `DetailSheet` 原本只能选择已有标签，没有创建入口。
4. Tauri 使用系统装饰标题栏，与应用深色主题不一致；侧栏是字母 G 占位图标。
5. `TranslationLaunchRouteResolver` 原本只接受工具绑定或 MTool 配方，不识别随游戏加载的 BepInEx 插件。
6. `LaunchPlanHandler` 丢失具体错误，`App.launch` 将错误显示在详情面板背后的主页面，两层问题叠加造成“没有反应”。

只读核对用户指定游戏后确认：Dungeon of Meat 有 `winhttp.dll`、BepInEx 核心、XUnity.AutoTranslator 的 BepInEx 与 Core DLL；但 `doorstop_config.ini` 的 `[General] enabled = false`。没有启动此游戏、修改配置或调用翻译服务。

[XUnity.AutoTranslator 官方安装说明](https://github.com/bbepis/XUnity.AutoTranslator/blob/master/README.md#bepinex-plugin)说明 BepInEx 版本通过启动游戏加载，不能把“必须翻译”误解为“必须启动外部翻译程序”。文件存在只是安装证据，不代表运行时翻译成功。

## 调用链

```text
手动扫描／周期扫描 → DirectoryWalker 系统目录剪枝 → 候选持久化二次防护
  → 接受候选 → GameCoverService → 磁盘 cover 复制到应用资产

导入封面 → 应用资产 → 无 cover：临时复制后原子改名；有 cover：不覆盖
完整扫描／周期核对 → 历史资产补齐 cover（启动快检不做大批图片复制）

Required → 显式工具绑定／内置插件／MTool 配方 → 原程序或工具路由
  └─ 缺失或禁用 → 保留具体错误 → 详情面板内显示
```

## 改动与优先级

- P0：扫描系统目录及旧待确认候选清理；恢复启动错误可见性；内置翻译识别。
- P1：`GameCoverService` 统一导入、扫描接受、手动建卡、周期核对的封面行为；已有文件不覆盖，不删除游戏文件；失败不阻断入库，直接导入会返回 warning。
- P1：详情页新建并分配 user 标签，重名复用既有 user 标签，保留原引擎标签语义。
- P2：新增 `TitleBar`，增加目录、扫描、设置按钮及窗口控制；保留旧侧栏入口。只授予最小窗口权限，按钮有键盘焦点和无障碍名称。
- P2：可编辑 SVG 卡匣图标，使用 `npx tauri icon public/app-icon.svg --output src-tauri/icons` 生成应用尺寸；同步 WPF/NSIS 使用的 `App.ico`。

组件影响：`AppShell` 增加标题栏槽与纵向外壳，`App` 接入现有控制器，`DetailSheet` 自己承接启动 Promise 和错误。没有新增路由，没有改动库查询与选择状态模型。

## 验收与边界

- 无规则、大小写变体、直接以系统目录为根及续扫都不能发现回收站游戏；旧待审核记录标为 ignored，不自动删除用户已接受的游戏。
- cover 保留原扩展名；不同扩展名的既有 cover 也不覆盖。已移除游戏、离线目录和目录联接不执行拷贝。
- 扫描接受 cover 后有独立应用副本；历史封面在完整扫描／周期核对补齐，无重复资产。
- Required 在插件与加载器齐全且未显式禁用时允许原 EXE；不执行插件安装或联网翻译，也不保证第三方插件的运行时兼容性。
- 无插件、缺加载器、禁用加载器必须拒绝并显示具体原因，不静默回退。
- 真实 WebView：窗口最大化／还原、详情标签创建、Required 错误可见、收藏／取消收藏、中文改名、批量操作与根目录移除。

## 回滚

可整体回退本次 commit，无 schema 回退需求。新生成的 cover 属于用户要求保存的便携副本，回退代码不会删除它们；既有 cover 和游戏程序始终保留。旧候选保留 ignored 记录，不做破坏性数据库清理。

## 验证记录

首轮 .NET 全量：505 项通过；其后新增禁用加载器、旧回收站候选测试，相关 25 项通过。真实桌面冒烟通过（`artifacts/build-reports/ux-six-smoke-a.png`）。最终复核结果完成后补记。
