using System.Diagnostics;
using System.Reflection;
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
}
