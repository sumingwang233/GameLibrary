# 演示版本记录

## 锁定版本

| 项目 | 值 |
| --- | --- |
| 源码版本 | `1.7.22` |
| 版本真源 | `Directory.Build.props:15` `<Version>1.7.22</Version>` |
| 提交 | `c6109ae18f4e2d31189fa26411da33bea4588e55`（短 `c6109ae`） |
| 提交时间 | 2026-10-17 21:32 +0800 |
| 提交说明 | `docs: record v1.7.22 installer delivery [skip ci]` |
| 获取日期 | 2026-10-20 |
| 分支状态 | `main`，`git describe` = `v1.7.16-12-gc6109ae`（HEAD 不在任何 tag 上） |
| 发行状态 | **未发布**：`README.md:13` 写明“待验收本地构建”，`docs/releases/v1.7.22.md:34` 为 `stableReady=false`、验证记录 `manualAcceptance=not-run`；远程无 `v1.7.17`–`v1.7.22` 的 tag 与 Release |
| 当前公开稳定版 | `v1.7.13`（README 下载入口） |
| 项目地址 | https://github.com/sumingwang233/GameLibrary |

配套版本号同一：`src/GameLibrary.Tauri/package.json:4`、`package-lock.json:3`、`src-tauri/Cargo.toml:3`、`Cargo.lock:1164-1165` 均为 `1.7.22`；`tauri.conf.json` 无版本字段。

## 画面上的版本标注

- 开场产品镜头（A 段末尾品牌字卡）与片尾卡均显示 `v1.7.22 · 开发版演示`。
- 片尾卡另有“可下载功能以发布版本为准”。
- 提交哈希只出现在本文件与 `promo/config/film.json` 的 `product.commit`，**不出现在画面中**。
- 不以“最新正式版”称呼该构建。

## 界面来源

产品画面来自锁定版本的真实前端组件（`src/GameLibrary.Tauri`，Vite 构建产物），运行在浏览器里，通过 `promo/ui-host/mock.js` 在浏览器侧实现 Tauri IPC，接入隔离的示例数据。没有启动真实后端、没有写入游戏目录、没有注入插件、没有启动游戏进程。

Unity 与 MTool 段落在真实组件上呈现“已配置 / 已生成启动配方，尚未验证游戏内翻译效果”等状态，未演示游戏内翻译效果。
