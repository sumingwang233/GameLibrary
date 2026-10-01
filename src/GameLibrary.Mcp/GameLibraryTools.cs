using System.ComponentModel;
using System.Text.Json;

using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GameLibrary.Mcp;

/// <summary>
/// 会话级配置：Main 解析 --data-dir 后注入，工具方法经 HostConnection 调用宿主。
/// </summary>
internal static class McpSession
{
    public static string? DataDirectory { get; set; }
}

[McpServerToolType]
public static partial class GameLibraryTools
{
    [McpServerTool(Name = "titles_translate")]
    [Description("将游戏名称翻译为简体中文，保留原文；只发送名称，不发送文件路径。引擎缺省使用设置。")]
    public static Task<CallToolResult> TitlesTranslate(
        [Description("游戏 ID 数组")] string[] gameIds,
        [Description("幂等键")] string idempotencyKey,
        [Description("balanced / google / bing；省略时使用设置")] string? engine = null,
        [Description("重新翻译已有译名")] bool force = false) =>
        engine is null
            ? InvokeOperationAsync("titles.translate", new { gameIds, idempotencyKey, force })
            : InvokeOperationAsync("titles.translate", new { gameIds, idempotencyKey, force, engine });

    [McpServerTool(Name = "capabilities_get")]
    [Description("列出 GameLibrary 可用操作、权限与宿主状态；宿主未连接时返回静态契约。")]
    public static async Task<CallToolResult> CapabilitiesGet()
    {
        if (McpSession.DataDirectory is not null
            && TryConnect(out var connection)
            && connection is not null)
        {
            try
            {
                var envelope = await connection.InvokeAsync(
                    new IpcRequest { RequestId = NewRequestId(), OperationId = "capabilities.get" },
                    CancellationToken.None);
                return ToToolResult(envelope);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        var catalog = OperationCatalog.Catalog;
        return ToToolResult(new Envelope<object>
        {
            RequestId = NewRequestId(),
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                apiVersion = catalog.ApiVersion,
                catalogVersion = catalog.CatalogVersion,
                hostConnected = false,
                availableOperations = catalog.AvailableOperations.Select(op => op.OperationId).ToArray(),
                plannedOperationsCount = catalog.Operations.Count - catalog.AvailableOperations.Count,
                permissions = catalog.Permissions,
            },
        });
    }

    [McpServerTool(Name = "schema_get")]
    [Description("返回指定操作的契约条目（CLI 命令、MCP 工具名、权限、Revision/幂等要求）。参数：operationId。")]
    public static Task<CallToolResult> SchemaGet([Description("操作 ID，如 games.update")] string operationId)
    {
        var info = OperationCatalog.Catalog.Find(operationId);
        if (info is null)
        {
            return Task.FromResult(ToToolResult(new Envelope<object>
            {
                RequestId = NewRequestId(),
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = $"未知操作：{operationId}",
                    Retryable = false,
                },
            }));
        }

        return Task.FromResult(ToToolResult(new Envelope<object>
        {
            RequestId = NewRequestId(),
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                operationId = info.OperationId,
                cli = info.Cli,
                mcpTool = info.McpTool,
                handler = info.Handler,
                permission = info.Permission,
                requiresRevision = info.RequiresRevision,
                requiresIdempotencyKey = info.RequiresIdempotencyKey,
                execution = info.Execution,
                available = info.IsAvailable,
                note = info.Note,
            },
        }));
    }

    [McpServerTool(Name = "host_status")]
    [Description("查询宿主运行状态；宿主未运行时按需拉起（连接引导层）。")]
    public static async Task<CallToolResult> HostStatus()
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest { RequestId = NewRequestId(), OperationId = "host.status" },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    [McpServerTool(Name = "events_read")]
    [Description("按游标增量读取库事件（候选发现/晋升、扫描完成、游戏入库）；游标过期返回 CursorExpired。参数：cursor（可选）、limit（可选）。")]
    public static Task<CallToolResult> EventsRead([Description("上次返回的 nextCursor")] long? cursor = null, [Description("返回条数上限")] int? limit = null) =>
        (cursor is null && limit is null)
            ? InvokeOperationAsync("events.read", new { })
            : InvokeOperationAsync("events.read", new { cursor, limit });

    [McpServerTool(Name = "scan_start")]
    [Description("对一个绝对本地路径启动只读扫描作业，返回受理的 jobId（status=accepted）。参数：root。")]
    public static async Task<CallToolResult> ScanStart([Description("扫描根的绝对本地路径")] string root, [Description("幂等键（可选，默认自动生成）")] string? idempotencyKey = null)
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = NewRequestId(),
                OperationId = "scan.start",
                Parameters = ToParameters(new { idempotencyKey = idempotencyKey ?? ("mcp-scan-" + Guid.NewGuid().ToString("N")), root }),
            },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    [McpServerTool(Name = "scan_status")]
    [Description("查询扫描作业状态（running/cancelRequested/cancelled/succeeded/failed）。参数：jobId。")]
    public static Task<CallToolResult> ScanStatus([Description("作业 ID")] string jobId) =>
        InvokeJobOperationAsync("scan.status", jobId);

    [McpServerTool(Name = "scan_cancel")]
    [Description("请求取消扫描作业；返回 cancelRequested，取消在检查点生效。参数：jobId。")]
    public static Task<CallToolResult> ScanCancel([Description("作业 ID")] string jobId) =>
        InvokeJobOperationAsync("scan.cancel", jobId);

    [McpServerTool(Name = "scan_coverage")]
    [Description("查询扫描作业的覆盖报告（运行中返回实时计数，完成后返回完整覆盖）。参数：jobId。")]
    public static Task<CallToolResult> ScanCoverage([Description("作业 ID")] string jobId) =>
        InvokeJobOperationAsync("scan.coverage", jobId);

    [McpServerTool(Name = "jobs_get")]
    [Description("查询任意作业的状态快照。参数：jobId。")]
    public static Task<CallToolResult> JobsGet([Description("作业 ID")] string jobId) =>
        InvokeJobOperationAsync("jobs.get", jobId);

    [McpServerTool(Name = "scan_inspect")]
    [Description("只读识别单个目录的引擎/入口证据，不创建候选、不启动作业。参数：path（绝对目录路径）。")]
    public static async Task<CallToolResult> ScanInspect([Description("待识别目录的绝对本地路径")] string path)
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = NewRequestId(),
                OperationId = "scan.inspect",
                Parameters = ToParameters(new { path }),
            },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    [McpServerTool(Name = "candidates_list")]
    [Description("列出扫描候选，可按作业或审核状态过滤并分页。参数：jobId、state、limit、offset（均可选）。")]
    public static async Task<CallToolResult> CandidatesList(
        [Description("按作业 ID 过滤；省略则不限作业")] string? jobId = null,
        [Description("按审核状态过滤，例如 pendingReview/accepted/deferred/ignored")] string? state = null,
        [Description("分页大小 1-1000；省略则返回全部匹配项")] int? limit = null,
        [Description("分页偏移；仅与 limit 一起生效")] int? offset = null)
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = NewRequestId(),
                OperationId = "candidates.list",
                Parameters = ToParameters(new { jobId, state, limit, offset }),
            },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    [McpServerTool(Name = "library_init")]
    [Description("在数据目录显式建库；重复执行返回错误。参数：idempotencyKey（建议提供，用于收据重放）。")]
    public static Task<CallToolResult> LibraryInit([Description("幂等键")] string? idempotencyKey = null) =>
        idempotencyKey is null
            ? InvokeOperationAsync("library.init", new { })
            : InvokeOperationAsync("library.init", new { idempotencyKey });

    [McpServerTool(Name = "translation_set")]
    [Description("设置翻译策略用户覆盖（Auto/Required/NotRequired）；只写覆盖层，继承值不动。Required 不回退为直启。参数：gameId、override、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> TranslationSet(
        [Description("游戏 ID")] string gameId,
        [Description("Auto/Required/NotRequired（区分大小写）")] string overrideValue,
        [Description("期望的游戏 Revision（乐观并发）")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("translation.set", new
        {
            idempotencyKey = idempotencyKey ?? $"transset-{Guid.NewGuid():N}",
            gameId,
            @override = overrideValue,
            expectedRevision,
        });

    [McpServerTool(Name = "games_update")]
    [Description("游戏受限字段 patch；当前仅支持 favorite。未知字段会被拒绝。参数：gameId、favorite、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> GamesUpdate(
        [Description("游戏 ID")] string gameId,
        [Description("收藏/取消收藏")] bool favorite,
        [Description("期望的游戏 Revision")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("games.update", new
        {
            idempotencyKey = idempotencyKey ?? $"gameupd-{Guid.NewGuid():N}",
            gameId,
            favorite,
            expectedRevision,
        });

    [McpServerTool(Name = "profiles_set_default")]
    [Description("把某 Profile 设为该游戏默认（显式替代项语义：新默认清除旧默认）。参数：gameId、profileId、idempotencyKey。")]
    public static Task<CallToolResult> ProfilesSetDefault(
        [Description("游戏 ID")] string gameId,
        [Description("Profile ID")] string profileId,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("profiles.set_default", new
        {
            idempotencyKey = idempotencyKey ?? $"setdef-{Guid.NewGuid():N}",
            gameId,
            profileId,
        });

    [McpServerTool(Name = "profiles_remove")]
    [Description("移除非默认 Profile；默认配置需先用 profiles_set_default 指定替代项。参数：profileId、idempotencyKey。")]
    public static Task<CallToolResult> ProfilesRemove(
        [Description("Profile ID")] string profileId,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("profiles.remove", new
        {
            idempotencyKey = idempotencyKey ?? $"rmprof-{Guid.NewGuid():N}",
            profileId,
        });

    [McpServerTool(Name = "games_relink")]
    [Description("重关联：把游戏的路径绑定改到新目录——仅改数据库，不移动/改名/复制文件。新路径须在已注册库根内且当前存在。参数：gameId、newPath、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> GamesRelink(
        [Description("游戏 ID")] string gameId,
        [Description("新目录的绝对本地路径")] string newPath,
        [Description("期望的游戏 Revision")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("games.relink", new
        {
            idempotencyKey = idempotencyKey ?? $"relink-{Guid.NewGuid():N}",
            gameId,
            newPath,
            expectedRevision,
        });

    [McpServerTool(Name = "notifications_list")]
    [Description("列出通知批（候选审核提醒等）；ack/defer 不等于接受候选。参数：state（pending/acknowledged/deferred，可选）。")]
    public static Task<CallToolResult> NotificationsList([Description("状态过滤（可选）")] string? state = null) =>
        state is null
            ? InvokeOperationAsync("notifications.list", new { })
            : InvokeOperationAsync("notifications.list", new { state });

    [McpServerTool(Name = "notifications_acknowledge")]
    [Description("把通知标记为已读；候选保持 pendingReview，接受仍需显式 candidates.accept。参数：notificationId、idempotencyKey。")]
    public static Task<CallToolResult> NotificationsAcknowledge(
        [Description("通知 ID")] string notificationId,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("notifications.acknowledge", new
        {
            idempotencyKey = idempotencyKey ?? $"ack-{notificationId}",
            notificationId,
        });

    [McpServerTool(Name = "notifications_defer")]
    [Description("把通知标记为稍后处理；默认不再主动提醒，全新候选才生成新通知。参数：notificationId、idempotencyKey。")]
    public static Task<CallToolResult> NotificationsDefer(
        [Description("通知 ID")] string notificationId,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("notifications.defer", new
        {
            idempotencyKey = idempotencyKey ?? $"def-{notificationId}",
            notificationId,
        });

    [McpServerTool(Name = "host_stop")]
    [Description("请求宿主优雅停机：响应送达后排空连接退出；不杀游戏/翻译器。参数：idempotencyKey。")]
    public static async Task<CallToolResult> HostStop([Description("幂等键")] string? idempotencyKey = null)
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = NewRequestId(),
                OperationId = "host.stop",
                Parameters = ToParameters(new { idempotencyKey = idempotencyKey ?? $"stop-{Guid.NewGuid():N}" }),
            },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    [McpServerTool(Name = "diagnostics_cache_rebuild")]
    [Description("清空可再生缓存目录（缩略图等派生物）；用户原图与游戏目录永不触碰。损坏缓存随删除自然重建。")]
    public static Task<CallToolResult> DiagnosticsCacheRebuild([Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("diagnostics.cache_rebuild", new
        {
            idempotencyKey = idempotencyKey ?? $"cache-{Guid.NewGuid():N}",
        });

    [McpServerTool(Name = "backups_create")]
    [Description("创建备份（作业）：SQLite 备份 API 一致快照 + 用户原图 + 哈希清单。返回 jobId。")]
    public static Task<CallToolResult> BackupsCreate([Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("backups.create", new { idempotencyKey = idempotencyKey ?? $"bkcreate-{Guid.NewGuid():N}" });

    [McpServerTool(Name = "backups_restore")]
    [Description("执行恢复（维护操作）：先备份当前状态，暂存替换，dataEpoch 续期使旧游标失效。参数：backupId、planId、idempotencyKey。")]
    public static Task<CallToolResult> BackupsRestore(
        [Description("备份 ID")] string backupId,
        [Description("恢复计划 ID（来自 backups_restore_plan）")] string planId,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("backups.restore", new
        {
            idempotencyKey = idempotencyKey ?? $"bkrestore-{Guid.NewGuid():N}",
            backupId,
            planId,
        });

    [McpServerTool(Name = "settings_update")]
    [Description("更新受限设置字段；未知字段拒绝。autostart 通过用户启动文件夹快捷方式实现（不写注册表）。参数：expectedRevision 与任意字段组合、idempotencyKey。")]
    public static Task<CallToolResult> SettingsUpdate(
        [Description("期望的设置 Revision")] int expectedRevision,
        [Description("激活视图 ID；清除请用 clearActiveView")] string? activeViewId = null,
        [Description("开机启动")] bool? autostartEnabled = null,
        [Description("周期核对间隔（分钟，1-10080）")] int? scanIntervalMinutes = null,
        [Description("主题 dark/light/system")] string? theme = null,
        [Description("关闭窗口缩到托盘")] bool? closeToTray = null,
        [Description("界面缩放，0.85-1.6")] double? uiFontScale = null,
        [Description("已安装字体的名称")] string? uiFontFamily = null,
        [Description("缓存存放位置的父目录")] string? cacheParentDirectory = null,
        [Description("恢复默认缓存位置")] bool clearCacheParentDirectory = false,
        [Description("清除激活视图")] bool clearActiveView = false,
        [Description("幂等键")] string? idempotencyKey = null,
        [Description("界面语言 zh-CN / zh-TW / en / ja")] string? uiLanguage = null,
        [Description("名称翻译引擎 balanced / google / bing")] string? titleTranslationEngine = null)
    {
        var patch = new Dictionary<string, object?>
        {
            ["idempotencyKey"] = idempotencyKey ?? $"setupd-{Guid.NewGuid():N}",
            ["expectedRevision"] = expectedRevision,
        };
        if (clearActiveView) patch["activeViewId"] = null;
        else if (activeViewId is not null) patch["activeViewId"] = activeViewId;
        if (autostartEnabled is not null) patch["autostartEnabled"] = autostartEnabled;
        if (scanIntervalMinutes is not null) patch["scanIntervalMinutes"] = scanIntervalMinutes;
        if (theme is not null) patch["theme"] = theme;
        if (closeToTray is not null) patch["closeToTray"] = closeToTray;
        if (uiFontScale is not null) patch["uiFontScale"] = uiFontScale;
        if (uiFontFamily is not null) patch["uiFontFamily"] = uiFontFamily;
        if (uiLanguage is not null) patch["uiLanguage"] = uiLanguage;
        if (titleTranslationEngine is not null) patch["titleTranslationEngine"] = titleTranslationEngine;
        if (clearCacheParentDirectory) patch["cacheParentDirectory"] = null;
        else if (cacheParentDirectory is not null) patch["cacheParentDirectory"] = cacheParentDirectory;
        return InvokeOperationAsync("settings.update", patch);
    }

    [McpServerTool(Name = "settings_reset")]
    [Description("恢复默认设置；开机启动一并关闭。参数：idempotencyKey。")]
    public static Task<CallToolResult> SettingsReset([Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("settings.reset", new { idempotencyKey = idempotencyKey ?? $"setreset-{Guid.NewGuid():N}" });

    [McpServerTool(Name = "views_create")]
    [Description("创建自定义视图（搜索/仅收藏/排序的语义状态）。参数：name、search、favoriteOnly、sort、idempotencyKey。")]
    public static Task<CallToolResult> ViewsCreate(
        [Description("视图名称")] string name,
        [Description("搜索词（可选）")] string? search = null,
        [Description("仅显示收藏")] bool favoriteOnly = false,
        [Description("排序：title/recent")] string? sort = null,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("views.create", new
        {
            idempotencyKey = idempotencyKey ?? $"viewnew-{Guid.NewGuid():N}",
            name,
            search,
            favoriteOnly,
            sort,
        });

    [McpServerTool(Name = "views_update")]
    [Description("更新自定义视图的受限字段；未提供字段保持不变。参数：viewId、expectedRevision、name/search/favoriteOnly/sort、idempotencyKey。")]
    public static Task<CallToolResult> ViewsUpdate(
        [Description("视图 ID")] string viewId,
        [Description("期望 Revision")] int expectedRevision,
        [Description("视图名称（可选）")] string? name = null,
        [Description("搜索词（可选）")] string? search = null,
        [Description("仅显示收藏（可选）")] bool? favoriteOnly = null,
        [Description("排序 title/recent（可选）")] string? sort = null,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("views.update", new
        {
            idempotencyKey = idempotencyKey ?? $"viewupd-{Guid.NewGuid():N}",
            viewId,
            name,
            search,
            favoriteOnly,
            sort,
            expectedRevision,
        });

    [McpServerTool(Name = "views_remove")]
    [Description("删除自定义视图；内置视图不可删除。参数：viewId、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> ViewsRemove(
        [Description("视图 ID")] string viewId,
        [Description("期望 Revision")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("views.remove", new
        {
            idempotencyKey = idempotencyKey ?? $"viewrm-{Guid.NewGuid():N}",
            viewId,
            expectedRevision,
        });

    [McpServerTool(Name = "views_activate")]
    [Description("激活视图（语义状态；广播 view.activated 事件）。参数：viewId、idempotencyKey（同视图同键幂等）。")]
    public static Task<CallToolResult> ViewsActivate(
        [Description("视图 ID")] string viewId,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("views.activate", new
        {
            idempotencyKey = idempotencyKey ?? $"viewact-{viewId}",
            viewId,
        });

    [McpServerTool(Name = "profiles_discover")]
    [Description("识别建议启动方式；只读游戏目录，不运行游戏。返回后台作业 ID。")]
    public static Task<CallToolResult> ProfilesDiscover(string idempotencyKey, string? gameId = null) =>
        InvokeOperationAsync("profiles.discover", new { idempotencyKey, gameId });

    [McpServerTool(Name = "profiles_restore")]
    [Description("手动恢复废弃启动方式；需要当前 revision。")]
    public static Task<CallToolResult> ProfilesRestore(string profileId, int expectedRevision, string idempotencyKey) =>
        InvokeOperationAsync("profiles.restore", new { profileId, expectedRevision, idempotencyKey });

    [McpServerTool(Name = "profiles_list")]
    [Description("列出启动配置（可按 gameId 过滤）。参数：gameId（可选）。")]
    public static Task<CallToolResult> ProfilesList([Description("按游戏 ID 过滤；省略则返回全部")] string? gameId = null) =>
        gameId is null
            ? InvokeOperationAsync("profiles.list", new { })
            : InvokeOperationAsync("profiles.list", new { gameId });

    [McpServerTool(Name = "launch_execute")]
    [Description("执行启动：全入口互斥、幂等键重放返回原尝试、Profile Revision 使旧计划失效。参数：idempotencyKey，planId 或 profileId（可选 expectedRevision）。")]
    public static Task<CallToolResult> LaunchExecute(
        [Description("幂等键：相同键重试返回原尝试")] string idempotencyKey,
        [Description("已生成的计划 ID（与 profileId 二选一）")] string? planId = null,
        [Description("直接按 Profile 启动（与 planId 二选一）")] string? profileId = null,
        [Description("按 profileId 启动时的期望 Revision")] int? expectedRevision = null)
    {
        object parameters = planId is not null
            ? new { idempotencyKey, planId }
            : new { idempotencyKey, profileId, expectedRevision };
        return InvokeOperationAsync("launch.execute", parameters);
    }

    [McpServerTool(Name = "launch_history")]
    [Description("查询启动尝试历史（可按 gameId 过滤）。参数：gameId（可选）。")]
    public static Task<CallToolResult> LaunchHistory([Description("按游戏 ID 过滤；省略则返回全部")] string? gameId = null) =>
        gameId is null
            ? InvokeOperationAsync("launch.history", new { })
            : InvokeOperationAsync("launch.history", new { gameId });

    [McpServerTool(Name = "games_create")]
    [Description("手动把未识别的游戏目录或独立 EXE/SWF/LNK 加入库；来源必须位于已注册游戏库内。响应提供可验证的启动建议，需用 profiles.create 保存。")]
    public static Task<CallToolResult> GamesCreate(
        [Description("游戏目录或独立 EXE/SWF/LNK 的绝对路径")] string sourcePath,
        [Description("卡片标题；省略时取路径名称")] string? title = null,
        [Description("幂等键；相同键重试不会重复建卡")] string? idempotencyKey = null) =>
        InvokeOperationAsync("games.create", new
        {
            idempotencyKey = idempotencyKey ?? $"gamecreate-{Guid.NewGuid():N}",
            sourcePath,
            title,
        });

    [McpServerTool(Name = "games_remove")]
    [Description("从库中移除游戏并登记忽略。默认保留文件；仅用户明确确认时传 deleteFiles=true 和 confirmedPath，将原文件移入回收站。")]
    public static Task<CallToolResult> GamesRemove(
        [Description("游戏 ID")] string gameId,
        [Description("期望 Revision")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null,
        [Description("是否明确确认删除原文件")] bool deleteFiles = false,
        [Description("用户确认的完整游戏路径")] string? confirmedPath = null) =>
        InvokeOperationAsync("games.remove", new
        {
            idempotencyKey = idempotencyKey ?? $"gameremove-{Guid.NewGuid():N}",
            gameId,
            expectedRevision,
            deleteFiles,
            confirmedPath,
        });

    [McpServerTool(Name = "diagnostics_logs")]
    [Description("读取最近的脱敏审计日志（分页）。参数：limit（1-1000，默认 100）。")]
    public static Task<CallToolResult> DiagnosticsLogs([Description("返回条数上限")] int? limit = null) =>
        limit is null
            ? InvokeOperationAsync("diagnostics.logs", new { })
            : InvokeOperationAsync("diagnostics.logs", new { limit });

    [McpServerTool(Name = "tools_discover")]
    [Description("只读发现指定目录的工具适配信息（MTool 配方 / RenpyThief 安装指纹与 Guided 计划）；不自启动工具。参数：path、tool（mtool 默认 / renpythief）。")]
    public static Task<CallToolResult> ToolsDiscover(
        [Description("游戏根或工具安装目录的绝对本地路径")] string path,
        [Description("工具类型：mtool（默认）或 renpythief")] string? tool = null) =>
        tool is null
            ? InvokeOperationAsync("tools.discover", new { idempotencyKey = $"discover-{Guid.NewGuid():N}", path })
            : InvokeOperationAsync("tools.discover", new { idempotencyKey = $"discover-{Guid.NewGuid():N}", path, tool });

    [McpServerTool(Name = "verification_invalidate")]
    [Description("使验证记录失效（工具更新/用户撤销）。参数：recordId。")]
    public static Task<CallToolResult> VerificationInvalidate([Description("验证记录 ID")] string recordId) =>
        InvokeOperationAsync("verification.invalidate", new { idempotencyKey = $"vinval-{Guid.NewGuid():N}", recordId });

    [McpServerTool(Name = "verification_list")]
    [Description("列出验证记录（可按 toolId 过滤）。参数：toolId（可选）。")]
    public static Task<CallToolResult> VerificationList([Description("按工具 ID 过滤")] string? toolId = null) =>
        toolId is null
            ? InvokeOperationAsync("verification.list", new { })
            : InvokeOperationAsync("verification.list", new { toolId });

    private static async Task<CallToolResult> InvokeOperationAsync(string operationId, object parameters)
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = NewRequestId(),
                OperationId = operationId,
                Parameters = ToParameters(parameters),
            },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    private static async Task<CallToolResult> InvokeJobOperationAsync(string operationId, string jobId)
    {
        if (McpSession.DataDirectory is null)
        {
            return ToToolResult(Failed("缺少 --data-dir 启动参数", ErrorCodes.InvalidArgument));
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            McpSession.DataDirectory, clientName: "mcp");
        var envelope = await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = NewRequestId(),
                OperationId = operationId,
                Parameters = ToParameters(new { jobId }),
            },
            CancellationToken.None);
        return ToToolResult(envelope);
    }

    private static System.Text.Json.JsonElement? ToParameters(object parameters)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(parameters, ContractJson.Options);
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Envelope<object> Failed(string message, string code) =>
        new()
        {
            RequestId = NewRequestId(),
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError { Code = code, Message = message, Retryable = false },
        };

    private static string NewRequestId() => $"mcp-{Guid.NewGuid():N}";

    private static bool TryConnect(out HostConnection? connection)
    {
        try
        {
            connection = HostConnection.ConnectAsync(McpSession.DataDirectory!, "mcp", CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch (HostClientException ex) when (ex.Code == HostClientErrorCodes.HostUnavailable)
        {
            connection = null;
            return false;
        }
    }

    /// <summary>信封 JSON 同时进入结构化内容与文本内容；业务失败置 isError（契约 6.2）。</summary>
    private static CallToolResult ToToolResult(Envelope<JsonElement> envelope) =>
        BuildResult(JsonSerializer.Serialize(envelope, ContractJson.Options), !envelope.Ok);

    private static CallToolResult ToToolResult(Envelope<object> envelope) =>
        BuildResult(JsonSerializer.Serialize(envelope, ContractJson.Options), !envelope.Ok);

    private static CallToolResult BuildResult(string json, bool isError) =>
        new()
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
        };
}
