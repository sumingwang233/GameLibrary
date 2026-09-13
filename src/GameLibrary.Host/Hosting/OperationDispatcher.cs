using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;

namespace GameLibrary.Host.Hosting;

/// <summary>宿主运行时身份：实例 ID、版本、启动时间；握手与 host.status 共用。</summary>
public sealed class HostIdentity
{
    public HostIdentity()
    {
        InstanceId = Guid.NewGuid().ToString("N");
        StartedAtUtc = DateTime.UtcNow;
        AppVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
    }

    public string InstanceId { get; }

    public string AppVersion { get; }

    public DateTime StartedAtUtc { get; }

    public int ProcessId => Environment.ProcessId;
}

/// <summary>T21 骨架分发器：host.status 可用；其余已在 catalog 登记但未实现，返回 UnsupportedOperation。</summary>
public sealed class OperationDispatcher
{
    private readonly HostIdentity _identity;

    public OperationDispatcher(HostIdentity identity)
    {
        _identity = identity;
    }

    public Envelope<object> Dispatch(IpcRequest request)
    {
        return request.OperationId switch
        {
            "host.status" => new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    hostInstanceId = _identity.InstanceId,
                    processId = _identity.ProcessId,
                    startedAtUtc = _identity.StartedAtUtc.ToString("O"),
                    appVersion = _identity.AppVersion,
                    apiVersion = ApiConstants.ApiVersion,
                    libraryInitialized = false,
                },
            },
            "capabilities.get" => new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = BuildCapabilities(),
            },
            "schema.get" => BuildSchema(request),
            _ => new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.UnsupportedOperation,
                    Message = $"操作 {request.OperationId} 已在 catalog 登记但尚未实现",
                    Retryable = false,
                },
            },
        };
    }

    private static object BuildCapabilities()
    {
        var catalog = OperationCatalog.Catalog;
        return new
        {
            apiVersion = catalog.ApiVersion,
            catalogVersion = catalog.CatalogVersion,
            appVersion = _identityAppVersion.Value,
            availableOperations = catalog.AvailableOperations.Select(op => op.OperationId).ToArray(),
            availableToolDetails = catalog.AvailableOperations.Select(op => new
            {
                operationId = op.OperationId,
                mcpTool = op.McpTool,
                cli = op.Cli,
                permission = op.Permission,
            }).ToArray(),
            plannedOperationsCount = catalog.Operations.Count - catalog.AvailableOperations.Count,
            permissions = catalog.Permissions,
            supportsCancellation = false,
            supportsEventSubscription = false,
        };
    }

    private static readonly Lazy<string> _identityAppVersion = new(() =>
        typeof(OperationDispatcher).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0");

    private static Envelope<object> BuildSchema(IpcRequest request)
    {
        string? operationId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty("operationId", out var operationElement)
            && operationElement.ValueKind == JsonValueKind.String)
        {
            operationId = operationElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            return InvalidArgument(request, "缺少 operationId 参数");
        }

        var info = OperationCatalog.Catalog.Find(operationId);
        if (info is null)
        {
            return InvalidArgument(request, $"未知操作：{operationId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
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
                inputSchemaFile = (string?)null,
                outputSchemaFile = (string?)null,
            },
        };
    }

    private static Envelope<object> InvalidArgument(IpcRequest request, string message) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.InvalidArgument,
                Message = message,
                Retryable = false,
            },
        };
}
