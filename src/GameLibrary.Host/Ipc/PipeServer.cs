using System.IO.Pipes;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host.Ipc;

/// <summary>
/// 命名管道服务端：同用户（CurrentUserOnly）、多客户端并发连接、单连接内串行处理。
/// 每连接先握手（版本校验），随后进入请求/响应循环；协议错误断开该连接不终止宿主。
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly HostRuntimeState _state;
    private readonly OperationDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _clients = [];
    private readonly object _clientsLock = new();
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
            NamedPipeServerStream pipe;
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
                break;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "管道服务在关停中停止接受连接");
                break;
            }

            var clientTask = Task.Run(() => ServeClientAsync(pipe, ct), ct);
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
            var handshake = await IpcFrame.ReadJsonAsync<HandshakeRequest>(pipe, ct);
            if (!string.Equals(handshake.ApiVersion, ApiConstants.ApiVersion, StringComparison.Ordinal))
            {
                _logger.LogWarning("客户端协议版本不匹配：{ApiVersion}", handshake.ApiVersion);
                await IpcFrame.WriteJsonAsync(pipe, new HandshakeResponse
                {
                    ApiVersion = handshake.ApiVersion,
                }, CancellationToken.None);
                return;
            }

            await IpcFrame.WriteJsonAsync(pipe, new HandshakeResponse
            {
                ApiVersion = ApiConstants.ApiVersion,
                HostInstanceId = _state.Identity.InstanceId,
                LibraryInstanceId = _state.Library.LibraryInstanceId,
                DataEpoch = _state.Library.DataEpoch,
                LibraryInitialized = _state.Library.Initialized,
                AppVersion = _state.Identity.AppVersion,
            }, ct);

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                IpcRequest request;
                try
                {
                    request = await IpcFrame.ReadJsonAsync<IpcRequest>(pipe, ct);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                // 收据 actor 与审计需要调用方标签：握手声明回填到每个请求。
                request.ClientName ??= handshake.ClientName;
                var envelope = SafeDispatch(request);
                await IpcFrame.WriteJsonAsync(pipe, envelope, ct);
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
