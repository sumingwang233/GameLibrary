using System.IO.Pipes;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Observability;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host.Ipc;

/// <summary>
/// 命名管道服务端：同用户（CurrentUserOnly）、多客户端并发连接、单连接内串行处理。
/// 每连接先握手（版本校验），随后进入请求/响应循环；协议错误断开该连接不终止宿主。
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private const int MaxConcurrentClients = 64;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleReadTimeout = TimeSpan.FromMinutes(2);

    private readonly string _pipeName;
    private readonly HostRuntimeState _state;
    private readonly OperationDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _clients = [];
    private readonly object _clientsLock = new();
    private int _activeClients;
    private Task? _acceptLoop;

    public PipeServer(string pipeName, HostRuntimeState state, OperationDispatcher dispatcher, ILogger logger)
    {
        _pipeName = pipeName;
        _state = state;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "接受管道连接失败，重试");
                try
                {
                    pipe?.Dispose();
                }
                catch (IOException)
                {
                }

                try
                {
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // 关键：把当前实例捕获到本次迭代的局部变量再交给任务——
            // 直接捕获循环变量 pipe 会在下一轮迭代被重新赋值，导致
            // 正在服务的任务引用被换成新管道（响应串台/连接错乱）。
            var connected = pipe;
            if (Interlocked.Increment(ref _activeClients) > MaxConcurrentClients)
            {
                Interlocked.Decrement(ref _activeClients);
                _logger.LogWarning("客户端连接数达到上限 {Limit}，拒绝新连接", MaxConcurrentClients);
                await connected.DisposeAsync();
                continue;
            }

            var clientTask = Task.Run(async () =>
            {
                try
                {
                    await ServeClientAsync(connected, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeClients);
                }
            }, CancellationToken.None);
            lock (_clientsLock)
            {
                _clients.Add(clientTask);
                _clients.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var handshake = await ReadJsonWithTimeoutAsync<HandshakeRequest>(pipe, ct, HandshakeTimeout);
            if (!string.Equals(handshake.ApiVersion, ApiConstants.ApiVersion, StringComparison.Ordinal))
            {
                _logger.LogWarning("客户端协议版本不匹配：{ApiVersion}", handshake.ApiVersion);
                await IpcFrame.WriteJsonAsync(pipe, new HandshakeResponse
                {
                    ApiVersion = handshake.ApiVersion,
                }, CancellationToken.None);
                return;
            }

            // 连接代数快照：library.init / backups.restore 成功后代数递增，
            // 下一轮循环检测到不一致即断开——旧纪元客户端必须重连获取新纪元（契约恢复语义）。
            int generationAtHandshake;
            HostLibraryState libraryAtHandshake;
            do
            {
                generationAtHandshake = Volatile.Read(ref _state.ConnectionGeneration);
                libraryAtHandshake = _state.Library;
            }
            while (generationAtHandshake != Volatile.Read(ref _state.ConnectionGeneration));

            var effectivePermissions = NormalizePermissions(handshake.Permissions);

            await IpcFrame.WriteJsonAsync(pipe, new HandshakeResponse
            {
                ApiVersion = ApiConstants.ApiVersion,
                HostInstanceId = _state.Identity.InstanceId,
                LibraryInstanceId = libraryAtHandshake.LibraryInstanceId,
                DataEpoch = libraryAtHandshake.DataEpoch,
                LibraryInitialized = libraryAtHandshake.Initialized,
                AppVersion = _state.Identity.AppVersion,
            }, ct);

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                if (Volatile.Read(ref _state.ConnectionGeneration) != generationAtHandshake)
                {
                    break;
                }

                IpcRequest request;
                try
                {
                    request = await ReadJsonWithTimeoutAsync<IpcRequest>(pipe, ct, IdleReadTimeout);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                // 收据 actor 与审计需要调用方标签：握手声明回填到每个请求；
                // 握手声明的权限集合同样回填（null=不限权，access.admin 永不由客户端自授）。
                request.ClientName ??= handshake.ClientName;
                request.GrantedPermissions = effectivePermissions;
                request.LibraryInstanceId ??= libraryAtHandshake.LibraryInstanceId;
                request.ExpectedDataEpoch ??= libraryAtHandshake.DataEpoch;

                if (Volatile.Read(ref _state.ConnectionGeneration) != generationAtHandshake)
                {
                    break;
                }

                var envelope = SafeDispatch(request);
                await IpcFrame.WriteJsonAsync(pipe, envelope, ct);

                // 库会话切换（init/restore）后旧连接立即失效：本轮响应已送达，主动断开。
                if (Volatile.Read(ref _state.ConnectionGeneration) != generationAtHandshake)
                {
                    _logger.LogInformation(
                        "库会话已切换（library.init/restore），断开旧纪元连接 client={Client}",
                        LogSanitizer.Sanitize(handshake.ClientName, _state.DataDirectory));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            _logger.LogDebug(ex, "客户端连接结束");
        }
        catch (Exception ex)
        {
            // 单连接故障不允许拖垮宿主进程或未观察任务异常。
            _logger.LogWarning(ex, "客户端连接意外失败");
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    private static async Task<T> ReadJsonWithTimeoutAsync<T>(
        NamedPipeServerStream pipe,
        CancellationToken shutdownToken,
        TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeoutCts.CancelAfter(timeout);
        return await IpcFrame.ReadJsonAsync<T>(pipe, timeoutCts.Token);
    }

    private static IReadOnlyList<string>? NormalizePermissions(IReadOnlyList<string>? declaredPermissions)
    {
        if (declaredPermissions is null)
        {
            return null;
        }

        // access.admin is a server-side capability. Treating it as a client-declared
        // permission would let a restricted same-user agent self-elevate.
        return declaredPermissions
            .Where(permission => !string.Equals(permission, "access.admin", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private Contracts.Envelope<object> SafeDispatch(IpcRequest request)
    {
        try
        {
            return _dispatcher.Dispatch(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "操作 {OperationId} 未捕获异常", request.OperationId);
            return new Contracts.Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InternalError,
                    Message = "宿主内部错误，详见日志",
                    Retryable = true,
                },
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] clients;
        lock (_clientsLock)
        {
            clients = [.. _clients];
        }

        await Task.WhenAll(clients);
        _shutdown.Dispose();
    }
}
