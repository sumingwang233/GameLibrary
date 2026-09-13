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

    [McpServerTool(Name = "scan_start")]
    [Description("对一个绝对本地路径启动只读扫描作业，返回受理的 jobId（status=accepted）。参数：root。")]
    public static async Task<CallToolResult> ScanStart([Description("扫描根的绝对本地路径")] string root)
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
                Parameters = ToParameters(new { root }),
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
    [Description("列出扫描发现的候选（可选按 jobId 过滤）；accept/defer/ignore 随入库任务提供。参数：jobId（可选）。")]
    public static async Task<CallToolResult> CandidatesList([Description("按作业 ID 过滤；省略则返回全部")] string? jobId = null)
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
                Parameters = jobId is null ? null : ToParameters(new { jobId }),
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

    [McpServerTool(Name = "library_init")]
    [Description("在数据目录显式建库；重复执行返回错误。参数：idempotencyKey（建议提供，用于收据重放）。")]
    public static Task<CallToolResult> LibraryInit([Description("幂等键")] string? idempotencyKey = null) =>
        idempotencyKey is null
            ? InvokeOperationAsync("library.init", new { })
            : InvokeOperationAsync("library.init", new { idempotencyKey });

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
    [Description("列出已入库游戏卡片。")]
    public static Task<CallToolResult> GamesList() =>
        InvokeOperationAsync("games.list", new { });

    [McpServerTool(Name = "games_get")]
    [Description("查询单个游戏卡片。参数：gameId。")]
    public static Task<CallToolResult> GamesGet([Description("游戏 ID")] string gameId) =>
        InvokeOperationAsync("games.get", new { gameId });

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
    [Description("只读发现指定游戏根的 MTool 适配信息：生成配方解析/断链标记/能力声明；不自启动工具。参数：path（游戏根绝对路径）。")]
    public static Task<CallToolResult> ToolsDiscover([Description("游戏根的绝对本地路径")] string path) =>
        InvokeOperationAsync("tools.discover", new { idempotencyKey = $"discover-{Guid.NewGuid():N}", path });

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
    [Description("导入封面图（≤1 MiB，png/jpg/webp/gif），复制入应用自有目录并设为当前封面；不反写游戏目录。参数：idempotencyKey、gameId、sourcePath。")]
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
    [Description("读取资产受限预览（≤1 MiB，base64）。参数：assetId。")]
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
