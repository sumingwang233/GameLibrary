# 收藏白屏与封面上限审计

## 根因

`GamesHandler.GamesUpdate` 返回 `{ gameId, favorite, revision }` 操作回执。`DetailSheet.toggleFavorite` 原本调用 `operation<GameItem>`，将该回执写入 `current`，下一轮渲染执行 `current.title.slice()` 时抛异常。TypeScript 泛型不验证运行时返回值，而异步事件处理器的 try/catch 不能捕获下一轮 React 渲染异常；根节点也没有错误边界，所以整个界面被卸载。

此前标题修改白屏是同一类问题：`fields.set` 的回执被当成游戏详情。上次修复只覆盖了标题入口，收藏入口遗漏。

```text
games.update → 操作回执 → 错当 GameItem → title 缺失 → 渲染异常 → 白屏
修复：games.update → games.get → 校验完整详情 → 更新界面
兜底：React ErrorBoundary → 错误提示与重新加载按钮
```

## 其他入口核对

- 标题、简介修改后已有重新读取；标签读取 `games.get`；批量更新不把回执写进游戏状态。
- 翻译策略的 `translation.set` 返回 `TranslationDto`，应用设置的 `settings.update` 返回 `SettingsDto`，与对应状态一致。
- 封面导入只使用 `assetId`，之后读取资产并刷新详情；启动配置操作也通过重新加载更新。
- 所有 `games.get/list` 响应在进入组件前校验必需字段，错误显示在既有错误区域；根节点增加渲染错误边界。

## 封面

1 MiB 是原有导入与读取硬限制，不是图片格式限制。提高为 5 MiB（5,242,880 字节），超过限制拒绝。缓存预览仍尽量保持小图，缓存不可用时可回退原图。IPC 上限从 4 MiB 提高到 8 MiB，覆盖 Base64 体积；使用 byte[] 的 JSON Base64 序列化避免 '+' 额外转义膨胀。同步更新 MCP 描述。

## 影响与验证

无数据库迁移、无路由改变，不修改已有游戏文件。回滚本次代码提交即可恢复原逻辑。回归检查覆盖详情收藏/取消收藏、中文标题保存、批量操作、5 MiB 原图回退、超限拒绝和后续连接可用性。

实际结果：Release 构建 0 警告/0 错误，全量 496 项 .NET 测试通过，格式检查及前端构建通过。真实 WebView 操作验证详情收藏/取消收藏、中文标题保存、批量收藏/标签/移除与目录清理均通过。5 MiB Base64 原图回退和超限拒绝经真实管道验证。
