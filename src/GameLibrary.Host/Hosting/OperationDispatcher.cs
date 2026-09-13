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

/// <summary>T21/T10 骨架分发器：host.status / capabilities.get / schema.get 可用；其余返回 UnsupportedOperation。</summary>
public sealed class OperationDispatcher
{
    private readonly HostRuntimeState _state;

    public OperationDispatcher(HostRuntimeState state)
    {
        _state = state;
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
                    hostInstanceId = _state.Identity.InstanceId,
                    processId = _state.Identity.ProcessId,
                    startedAtUtc = _state.Identity.StartedAtUtc.ToString("O"),
                    appVersion = _state.Identity.AppVersion,
                    apiVersion = ApiConstants.ApiVersion,
                    libraryInitialized = _state.Library.Initialized,
                    libraryState = _state.Library.Status.ToString(),
                    libraryInstanceId = _state.Library.LibraryInstanceId,
                    dataEpoch = _state.Library.DataEpoch,
                    schemaVersion = _state.Library.SchemaVersion,
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
            "scan.start" => ScanStart(request),
            "scan.status" or "jobs.get" => JobSnapshotEnvelope(request, request.OperationId == "scan.status" ? "scan" : null),
            "scan.cancel" => ScanCancel(request),
            "scan.coverage" => ScanCoverage(request),
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

    private Envelope<object> ScanStart(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "root", out var root))
        {
            return InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(root);
        if (!validation.IsValid)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = validation.IsUnsupported ? ErrorCodes.UnsupportedPath : ErrorCodes.InvalidPath,
                    Message = $"根路径非法（{validation.Reason}）：{root}",
                    Retryable = false,
                },
            };
        }

        var rootPath = validation.Path!;
        if (!Directory.Exists(rootPath.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"根路径不存在或离线：{rootPath.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        var jobId = _state.Jobs.Create(
            "scan",
            context => Task.FromResult(Scanning.ScanJobRunner.Run(rootPath, context)));

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, kind = "scan", state = "running" },
        };
    }

    private Envelope<object> JobSnapshotEnvelope(IpcRequest request, string? expectedKind)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        var snapshot = _state.Jobs.Get(jobId);
        if (snapshot is null)
        {
            return NotFound(request, $"作业不存在：{jobId}");
        }

        if (expectedKind is not null && !string.Equals(snapshot.Kind, expectedKind, StringComparison.Ordinal))
        {
            return InvalidArgument(request, $"作业 {jobId} 类型是 {snapshot.Kind}，不是 {expectedKind}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                jobId = snapshot.JobId,
                kind = snapshot.Kind,
                state = snapshot.State,
                createdUtc = snapshot.CreatedUtc.ToString("O"),
                startedUtc = snapshot.StartedUtc?.ToString("O"),
                finishedUtc = snapshot.FinishedUtc?.ToString("O"),
                error = snapshot.Error,
            },
        };
    }

    private Envelope<object> ScanCancel(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        if (!_state.Jobs.RequestCancel(jobId))
        {
            return _state.Jobs.Get(jobId) is null
                ? NotFound(request, $"作业不存在：{jobId}")
                : InvalidArgument(request, $"作业已进入终态，无法取消：{jobId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { jobId, state = "cancelRequested" },
        };
    }

    private Envelope<object> ScanCoverage(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        var progress = _state.Jobs.TryGetProgress(jobId);
        if (progress is null)
        {
            return NotFound(request, $"作业不存在：{jobId}");
        }

        var (state, data) = progress.Value;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                jobId,
                state,
                coverage = data,
            },
        };
    }

    private static bool TryGetStringParameter(IpcRequest request, string name, out string value)
    {
        value = "";
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            return value.Length > 0;
        }

        return false;
    }

    private static Envelope<object> NotFound(IpcRequest request, string message) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.NotFound,
                Message = message,
                Retryable = false,
            },
        };

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
