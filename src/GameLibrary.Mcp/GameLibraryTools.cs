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
