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
