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
public static class GameLibraryTools
{
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

    [McpServerTool(Name = "candidates_get")]
    [Description("查询单个候选详情：引擎证据、入口候选、祖先分类与审核状态。参数：candidateId。")]
    public static Task<CallToolResult> CandidatesGet([Description("候选 ID")] string candidateId) =>
        InvokeOperationAsync("candidates.get", new { candidateId });

    [McpServerTool(Name = "roots_add")]
    [Description("注册库根（显式授权）：此后 scan/启动目标必须落在已注册根内。参数：root（绝对本地路径）。")]
    public static Task<CallToolResult> RootsAdd([Description("库根的绝对本地路径")] string root) =>
        InvokeOperationAsync("roots.add", new { root });

    [McpServerTool(Name = "roots_list")]
    [Description("列出已注册库根。")]
    public static Task<CallToolResult> RootsList() =>
        InvokeOperationAsync("roots.list", new { });

    [McpServerTool(Name = "roots_remove")]
    [Description("移除库根（仅解除扫描/启动边界，不触碰游戏数据与记录）。参数：rootId、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> RootsRemove(
        [Description("库根 ID")] string rootId,
        [Description("期望库根修订")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("roots.remove", new { idempotencyKey, rootId, expectedRevision });

    [McpServerTool(Name = "tags_list")]
    [Description("列出全部标签（含游戏计数；kind=engine 为自动标签，user 为用户标签）。")]
    public static Task<CallToolResult> TagsList() =>
        InvokeOperationAsync("tags.list", new { });

    [McpServerTool(Name = "tags_create")]
    [Description("创建用户标签。参数：name、color（可选 #RRGGBB）、idempotencyKey。")]
    public static Task<CallToolResult> TagsCreate(
        [Description("标签名（1–100 字符）")] string name,
        [Description("颜色 #RRGGBB（可选）")] string? color = null,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.create", new { idempotencyKey, name, color });

    [McpServerTool(Name = "tags_update")]
    [Description("更新用户标签（自动标签不可编辑）。参数：tagId、name/color（可选）、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> TagsUpdate(
        [Description("标签 ID")] string tagId,
        [Description("期望修订")] int expectedRevision,
        [Description("新名称（可选）")] string? name = null,
        [Description("新颜色 #RRGGBB（可选）")] string? color = null,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.update", new { idempotencyKey, tagId, name, color, expectedRevision });

    [McpServerTool(Name = "tags_remove")]
    [Description("删除标签并返回受影响游戏列表；删除自动标签会逐游戏登记 Suppress（重扫不恢复）。参数：tagId、expectedRevision、idempotencyKey。")]
    public static Task<CallToolResult> TagsRemove(
        [Description("标签 ID")] string tagId,
        [Description("期望修订")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.remove", new { idempotencyKey, tagId, expectedRevision });

    [McpServerTool(Name = "tags_assign")]
    [Description("把标签挂到游戏（幂等；同时清除该标签的 Suppress）。参数：gameId、tagId、expectedRevision（游戏修订）、idempotencyKey。")]
    public static Task<CallToolResult> TagsAssign(
        [Description("游戏 ID")] string gameId,
        [Description("标签 ID")] string tagId,
        [Description("期望游戏修订")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.assign", new { idempotencyKey, gameId, tagId, expectedRevision });

    [McpServerTool(Name = "tags_unassign")]
    [Description("解除游戏标签；自动标签解除后登记 Suppress（重扫不恢复）。参数：gameId、tagId、expectedRevision（游戏修订）、idempotencyKey。")]
    public static Task<CallToolResult> TagsUnassign(
        [Description("游戏 ID")] string gameId,
        [Description("标签 ID")] string tagId,
        [Description("期望游戏修订")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.unassign", new { idempotencyKey, gameId, tagId, expectedRevision });

    [McpServerTool(Name = "tags_suppress")]
    [Description("抑制 (游戏, 标签)：阻止扫描恢复用户删除的自动标签。参数：gameId、tagId、expectedRevision（游戏修订）、idempotencyKey。")]
    public static Task<CallToolResult> TagsSuppress(
        [Description("游戏 ID")] string gameId,
        [Description("标签 ID")] string tagId,
        [Description("期望游戏修订")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.suppress", new { idempotencyKey, gameId, tagId, expectedRevision });

    [McpServerTool(Name = "tags_reset")]
    [Description("清除 (游戏, 自动标签) 的 Suppress 并按当前引擎恢复。参数：gameId、tagId、expectedRevision（游戏修订）、idempotencyKey。")]
    public static Task<CallToolResult> TagsReset(
        [Description("游戏 ID")] string gameId,
        [Description("标签 ID")] string tagId,
        [Description("期望游戏修订")] int expectedRevision,
        [Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("tags.reset", new { idempotencyKey, gameId, tagId, expectedRevision });

    [McpServerTool(Name = "library_init")]
    [Description("在数据目录显式建库；重复执行返回错误。参数：idempotencyKey（建议提供，用于收据重放）。")]
    public static Task<CallToolResult> LibraryInit([Description("幂等键")] string? idempotencyKey = null) =>
        idempotencyKey is null
            ? InvokeOperationAsync("library.init", new { })
            : InvokeOperationAsync("library.init", new { idempotencyKey });

    [McpServerTool(Name = "translation_get")]
    [Description("查询游戏翻译策略：继承值（[toolNeed] 祖先）、用户覆盖与有效值分离返回。参数：gameId。")]
    public static Task<CallToolResult> TranslationGet([Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("translation.get", new { gameId });

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

    [McpServerTool(Name = "profiles_validate")]
    [Description("校验 Profile：入口/工作目录存在性；不执行任何程序。参数：profileId。")]
    public static Task<CallToolResult> ProfilesValidate([Description("Profile ID")] string profileId) =>
        InvokeOperationAsync("profiles.validate", new { profileId });

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

    [McpServerTool(Name = "notifications_get")]
    [Description("查询单个通知批详情（含候选 ID 集合）。参数：notificationId。")]
    public static Task<CallToolResult> NotificationsGet([Description("通知 ID")] string notificationId) =>
        InvokeOperationAsync("notifications.get", new { notificationId });

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

    [McpServerTool(Name = "backups_list")]
    [Description("列出可用备份（含库快照与用户原图，逐文件 SHA-256 清单）。")]
    public static Task<CallToolResult> BackupsList() =>
        InvokeOperationAsync("backups.list", new { });

    [McpServerTool(Name = "backups_create")]
    [Description("创建备份（作业）：SQLite 备份 API 一致快照 + 用户原图 + 哈希清单。返回 jobId。")]
    public static Task<CallToolResult> BackupsCreate([Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("backups.create", new { idempotencyKey = idempotencyKey ?? $"bkcreate-{Guid.NewGuid():N}" });

    [McpServerTool(Name = "backups_inspect")]
    [Description("备份完整性核查：逐文件大小与 SHA-256 校验。参数：backupId。")]
    public static Task<CallToolResult> BackupsInspect([Description("备份 ID")] string backupId) =>
        InvokeOperationAsync("backups.inspect", new { backupId });

    [McpServerTool(Name = "backups_restore_plan")]
    [Description("生成恢复影响计划（10 分钟有效）：恢复后的实例/epoch 变化与资产数量。参数：backupId。")]
    public static Task<CallToolResult> BackupsRestorePlan([Description("备份 ID")] string backupId) =>
        InvokeOperationAsync("backups.restore_plan", new { backupId });

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

    [McpServerTool(Name = "settings_get")]
    [Description("读取应用设置快照：激活视图、开机启动、核对周期、主题、字体、界面缩放、缓存位置、托盘行为与 Revision。")]
    public static Task<CallToolResult> SettingsGet() =>
        InvokeOperationAsync("settings.get", new { });

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
        [Description("幂等键")] string? idempotencyKey = null)
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
        if (clearCacheParentDirectory) patch["cacheParentDirectory"] = null;
        else if (cacheParentDirectory is not null) patch["cacheParentDirectory"] = cacheParentDirectory;
        return InvokeOperationAsync("settings.update", patch);
    }

    [McpServerTool(Name = "settings_reset")]
    [Description("恢复默认设置；开机启动一并关闭。参数：idempotencyKey。")]
    public static Task<CallToolResult> SettingsReset([Description("幂等键")] string? idempotencyKey = null) =>
        InvokeOperationAsync("settings.reset", new { idempotencyKey = idempotencyKey ?? $"setreset-{Guid.NewGuid():N}" });

    [McpServerTool(Name = "views_list")]
    [Description("列出内置与自定义视图及当前激活视图。")]
    public static Task<CallToolResult> ViewsList() =>
        InvokeOperationAsync("views.list", new { });

    [McpServerTool(Name = "views_get")]
    [Description("查询单个视图定义（筛选/排序语义）。参数：viewId。")]
    public static Task<CallToolResult> ViewsGet([Description("视图 ID")] string viewId) =>
        InvokeOperationAsync("views.get", new { viewId });

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

    [McpServerTool(Name = "profiles_create")]
    [Description("创建启动配置（最小集）：绝对 exe、argv 数组、绝对 cwd。参数：idempotencyKey、gameId、executablePath、argv、cwd。")]
    public static Task<CallToolResult> ProfilesCreate(
        [Description("幂等键：相同键重试返回原结果")] string idempotencyKey,
        [Description("所属游戏 ID")] string gameId,
        [Description("启动目标的绝对路径")] string executablePath,
        [Description("argv 参数数组")] string[] argv,
        [Description("工作目录绝对路径")] string cwd) =>
        InvokeOperationAsync("profiles.create", new { idempotencyKey, gameId, executablePath, argv, cwd });

    [McpServerTool(Name = "profiles_list")]
    [Description("列出启动配置（可按 gameId 过滤）。参数：gameId（可选）。")]
    public static Task<CallToolResult> ProfilesList([Description("按游戏 ID 过滤；省略则返回全部")] string? gameId = null) =>
        gameId is null
            ? InvokeOperationAsync("profiles.list", new { })
            : InvokeOperationAsync("profiles.list", new { gameId });

    [McpServerTool(Name = "profiles_get")]
    [Description("查询单个启动配置。参数：profileId。")]
    public static Task<CallToolResult> ProfilesGet([Description("Profile ID")] string profileId) =>
        InvokeOperationAsync("profiles.get", new { profileId });

    [McpServerTool(Name = "profiles_update")]
    [Description("更新启动配置；expectedRevision 不一致返回 RevisionConflict，更新使引用旧 Revision 的计划失效。参数：idempotencyKey、profileId、executablePath、argv、cwd、expectedRevision。")]
    public static Task<CallToolResult> ProfilesUpdate(
        [Description("幂等键：相同键重试返回原结果")] string idempotencyKey,
        [Description("Profile ID")] string profileId,
        [Description("启动目标的绝对路径")] string executablePath,
        [Description("argv 参数数组")] string[] argv,
        [Description("工作目录绝对路径")] string cwd,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("profiles.update", new { idempotencyKey, profileId, executablePath, argv, cwd, expectedRevision });

    [McpServerTool(Name = "launch_plan")]
    [Description("生成纯数据启动计划（可预览，无副作用）。参数：gameId、profileId。")]
    public static Task<CallToolResult> LaunchPlan([Description("游戏 ID")] string gameId, [Description("Profile ID")] string profileId) =>
        InvokeOperationAsync("launch.plan", new { gameId, profileId });

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

    [McpServerTool(Name = "launch_status")]
    [Description("查询启动尝试状态（prepared/executing/processCreated/exited/processStartFailed）。参数：attemptId。")]
    public static Task<CallToolResult> LaunchStatus([Description("尝试 ID")] string attemptId) =>
        InvokeOperationAsync("launch.status", new { attemptId });

    [McpServerTool(Name = "launch_history")]
    [Description("查询启动尝试历史（可按 gameId 过滤）。参数：gameId（可选）。")]
    public static Task<CallToolResult> LaunchHistory([Description("按游戏 ID 过滤；省略则返回全部")] string? gameId = null) =>
        gameId is null
            ? InvokeOperationAsync("launch.history", new { })
            : InvokeOperationAsync("launch.history", new { gameId });

    [McpServerTool(Name = "candidates_accept")]
    [Description("接受候选入库（仅 pendingReview）：创建游戏卡片并返回 gameId；同候选重试幂等返回已有 gameId。参数：idempotencyKey、candidateId、expectedRevision。")]
    public static Task<CallToolResult> CandidatesAccept(
        [Description("幂等键")] string idempotencyKey,
        [Description("候选 ID")] string candidateId,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("candidates.accept", new { idempotencyKey, candidateId, expectedRevision });

    [McpServerTool(Name = "candidates_defer")]
    [Description("暂缓候选（仅 pendingReview）；deferred 需人工重新查看，不周期重弹。参数：idempotencyKey、candidateId、expectedRevision。")]
    public static Task<CallToolResult> CandidatesDefer(
        [Description("幂等键")] string idempotencyKey,
        [Description("候选 ID")] string candidateId,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("candidates.defer", new { idempotencyKey, candidateId, expectedRevision });

    [McpServerTool(Name = "candidates_ignore")]
    [Description("忽略候选（仅 pendingReview）：同时登记 ExactPath 忽略规则；撤销规则才恢复提示。参数：idempotencyKey、candidateId、expectedRevision。")]
    public static Task<CallToolResult> CandidatesIgnore(
        [Description("幂等键")] string idempotencyKey,
        [Description("候选 ID")] string candidateId,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("candidates.ignore", new { idempotencyKey, candidateId, expectedRevision });

    [McpServerTool(Name = "games_list")]
    [Description("列出已入库游戏卡片，可搜索、过滤、排序并分页。参数均可选。")]
    public static Task<CallToolResult> GamesList(
        [Description("搜索标题或原路径")] string? search = null,
        [Description("true 时仅返回收藏游戏")] bool? favorite = null,
        [Description("排序：title/title-asc、title-desc、recent/updated-desc、accepted-desc")] string? sort = null,
        [Description("套用已保存视图")] string? viewId = null,
        [Description("按标签 ID 过滤")] string? tagId = null,
        [Description("分页大小 1-1000；省略则返回全部匹配项")] int? limit = null,
        [Description("分页偏移；仅与 limit 一起生效")] int? offset = null) =>
        InvokeOperationAsync("games.list", new { search, favorite, sort, viewId, tagId, limit, offset });

    [McpServerTool(Name = "games_get")]
    [Description("查询单个游戏卡片。参数：gameId。")]
    public static Task<CallToolResult> GamesGet([Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("games.get", new { gameId });

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

    [McpServerTool(Name = "ignores_list")]
    [Description("列出忽略规则。")]
    public static Task<CallToolResult> IgnoresList() =>
        InvokeOperationAsync("ignores.list", new { });

    [McpServerTool(Name = "ignores_create")]
    [Description("创建忽略规则（scope: ExactPath/Subtree/ConfirmedIdentity），立即抑制匹配的待审核候选。参数：idempotencyKey、scope、path 或 gameId、reason（可选）。")]
    public static Task<CallToolResult> IgnoresCreate(
        [Description("幂等键")] string idempotencyKey,
        [Description("范围：ExactPath/Subtree/ConfirmedIdentity")] string scope,
        [Description("ExactPath/Subtree 的规范化绝对路径")] string? path = null,
        [Description("ConfirmedIdentity 绑定的用户确认 gameId")] string? gameId = null,
        [Description("原因说明")] string? reason = null) =>
        InvokeOperationAsync("ignores.create", new { idempotencyKey, scope, path, gameId, reason });

    [McpServerTool(Name = "ignores_remove")]
    [Description("撤销忽略规则（恢复候选提示的唯一途径）：匹配的 ignored 候选回到 observed。参数：idempotencyKey、ignoreId、expectedRevision（可选）。")]
    public static Task<CallToolResult> IgnoresRemove(
        [Description("幂等键")] string idempotencyKey,
        [Description("忽略规则 ID")] string ignoreId,
        [Description("期望 Revision（可选）")] int? expectedRevision = null) =>
        InvokeOperationAsync("ignores.remove", new { idempotencyKey, ignoreId, expectedRevision });

    [McpServerTool(Name = "diagnostics_status")]
    [Description("诊断状态：进程/库状态/审计日志统计与活动作业数；不含业务数据原文。")]
    public static Task<CallToolResult> DiagnosticsStatus() =>
        InvokeOperationAsync("diagnostics.status", new { });

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

    [McpServerTool(Name = "verification_start")]
    [Description("开始一次工具验证：绑定工具指纹与隔离样本，状态 Unknown；样本必须来自用户授权的隔离副本。参数：idempotencyKey、toolId、fingerprint、engine、samplePath。")]
    public static Task<CallToolResult> VerificationStart(
        [Description("幂等键")] string idempotencyKey,
        [Description("工具 ID（mtool/renpythief）")] string toolId,
        [Description("工具指纹")] string fingerprint,
        [Description("样本游戏引擎标识")] string engine,
        [Description("隔离样本路径")] string samplePath) =>
        InvokeOperationAsync("verification.start", new { idempotencyKey, toolId, fingerprint, engine, samplePath });

    [McpServerTool(Name = "verification_report")]
    [Description("提交验证观察：游戏启动与翻译生效双结论分开累积；翻译生效必须先有游戏启动证据。参数：idempotencyKey、recordId、gameStarted、translationConfirmed、fingerprint（可选校验）。")]
    public static Task<CallToolResult> VerificationReport(
        [Description("幂等键")] string idempotencyKey,
        [Description("验证记录 ID")] string recordId,
        [Description("游戏已启动（用户观察）")] bool gameStarted,
        [Description("翻译实际生效（用户观察）")] bool translationConfirmed = false,
        [Description("工具指纹（可选，不一致即失效）")] string? fingerprint = null) =>
        InvokeOperationAsync("verification.report", new { idempotencyKey, recordId, gameStarted, translationConfirmed, fingerprint });

    [McpServerTool(Name = "verification_invalidate")]
    [Description("使验证记录失效（工具更新/用户撤销）。参数：recordId。")]
    public static Task<CallToolResult> VerificationInvalidate([Description("验证记录 ID")] string recordId) =>
        InvokeOperationAsync("verification.invalidate", new { idempotencyKey = $"vinval-{Guid.NewGuid():N}", recordId });

    [McpServerTool(Name = "verification_get")]
    [Description("查询验证记录。参数：recordId。")]
    public static Task<CallToolResult> VerificationGet([Description("验证记录 ID")] string recordId) =>
        InvokeOperationAsync("verification.get", new { recordId });

    [McpServerTool(Name = "verification_list")]
    [Description("列出验证记录（可按 toolId 过滤）。参数：toolId（可选）。")]
    public static Task<CallToolResult> VerificationList([Description("按工具 ID 过滤")] string? toolId = null) =>
        toolId is null
            ? InvokeOperationAsync("verification.list", new { })
            : InvokeOperationAsync("verification.list", new { toolId });

    [McpServerTool(Name = "fields_set")]
    [Description("设置游戏资料字段（title/summary，用户来源）；Revision 即游戏卡片 Revision。参数：idempotencyKey、gameId、field、value、expectedRevision。")]
    public static Task<CallToolResult> FieldsSet(
        [Description("幂等键")] string idempotencyKey,
        [Description("游戏 ID")] string gameId,
        [Description("字段名：title 或 summary")] string field,
        [Description("字段值（不传 = 清空）")] string? value = null,
        [Description("期望 Revision")] int expectedRevision = 0) =>
        InvokeOperationAsync("fields.set", new { idempotencyKey, gameId, field, value, expectedRevision });

    [McpServerTool(Name = "assets_import")]
    [Description("导入封面图（≤5 MiB，png/jpg/webp/gif），复制入应用自有目录并设为当前封面；游戏目录没有 cover 时补拷贝 cover.原扩展名，已有文件不覆盖。参数：idempotencyKey、gameId、sourcePath。")]
    public static Task<CallToolResult> AssetsImport(
        [Description("幂等键")] string idempotencyKey,
        [Description("游戏 ID")] string gameId,
        [Description("源图片绝对路径")] string sourcePath) =>
        InvokeOperationAsync("assets.import", new { idempotencyKey, gameId, sourcePath });

    [McpServerTool(Name = "fields_clear")]
    [Description("用户主动清空资料字段（value=null，≠继承自动值）。参数：idempotencyKey、gameId、field、expectedRevision。")]
    public static Task<CallToolResult> FieldsClear(
        [Description("幂等键")] string idempotencyKey,
        [Description("游戏 ID")] string gameId,
        [Description("字段名：title 或 summary")] string field,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("fields.clear", new { idempotencyKey, gameId, field, expectedRevision });

    [McpServerTool(Name = "fields_reset")]
    [Description("恢复资料字段的自动值（删除用户覆盖层）。参数：idempotencyKey、gameId、field、expectedRevision。")]
    public static Task<CallToolResult> FieldsReset(
        [Description("幂等键")] string idempotencyKey,
        [Description("游戏 ID")] string gameId,
        [Description("字段名：title 或 summary")] string field,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("fields.reset", new { idempotencyKey, gameId, field, expectedRevision });

    [McpServerTool(Name = "assets_choose")]
    [Description("选择某资产为当前封面。参数：idempotencyKey、gameId、assetId、expectedRevision。")]
    public static Task<CallToolResult> AssetsChoose(
        [Description("幂等键")] string idempotencyKey,
        [Description("游戏 ID")] string gameId,
        [Description("资产 ID")] string assetId,
        [Description("期望 Revision")] int expectedRevision) =>
        InvokeOperationAsync("assets.choose", new { idempotencyKey, gameId, assetId, expectedRevision });

    [McpServerTool(Name = "assets_crop")]
    [Description("像素级裁切封面并产出新资产（设为当前封面）。参数：idempotencyKey、assetId、x、y、width、height。")]
    public static Task<CallToolResult> AssetsCrop(
        [Description("幂等键")] string idempotencyKey,
        [Description("源资产 ID")] string assetId,
        [Description("裁切起点 X")] int x,
        [Description("裁切起点 Y")] int y,
        [Description("裁切宽度")] int width,
        [Description("裁切高度")] int height) =>
        InvokeOperationAsync("assets.crop", new { idempotencyKey, assetId, x, y, width, height });

    [McpServerTool(Name = "assets_reset")]
    [Description("重置封面：全部封面置为非当前，游戏回到无封面展示。参数：gameId。")]
    public static Task<CallToolResult> AssetsReset([Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("assets.reset", new { gameId });

    [McpServerTool(Name = "assets_remove")]
    [Description("移除非当前引用的应用自有资产。参数：idempotencyKey、assetId。")]
    public static Task<CallToolResult> AssetsRemove(
        [Description("幂等键")] string idempotencyKey,
        [Description("资产 ID")] string assetId) =>
        InvokeOperationAsync("assets.remove", new { idempotencyKey, assetId });

    [McpServerTool(Name = "metadata_preview")]
    [Description("本地证据元数据建议预览（仅 auto，无在线源）。参数：gameId。")]
    public static Task<CallToolResult> MetadataPreview([Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("metadata.preview", new { gameId });

    [McpServerTool(Name = "metadata_refresh")]
    [Description("刷新元数据自动值（作业；只更新 AutoValue 不覆盖用户层）。参数：idempotencyKey、gameId。")]
    public static Task<CallToolResult> MetadataRefresh(
        [Description("幂等键")] string idempotencyKey,
        [Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("metadata.refresh", new { idempotencyKey, gameId });

    [McpServerTool(Name = "assets_list")]
    [Description("列出游戏资产（cover）。参数：gameId。")]
    public static Task<CallToolResult> AssetsList([Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("assets.list", new { gameId });

    [McpServerTool(Name = "assets_get")]
    [Description("读取资产预览，缓存不可用时返回原图（≤5 MiB，base64）。参数：assetId。")]
    public static Task<CallToolResult> AssetsGet([Description("资产 ID")] string assetId) =>
        InvokeOperationAsync("assets.get", new { assetId });

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
