using System.IO.Pipes;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;

namespace GameLibrary.HostClient;

/// <summary>
/// 三入口共用的类型化 IPC 客户端（ADR-0007）。不含业务规则；
/// 单连接内请求串行，一个客户端实例一次只发一个请求。
/// </summary>
public sealed class HostConnection : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private HandshakeResponse? _handshake;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;
    private readonly string _dataDirectory;
    private readonly string? _clientName;
    private readonly IReadOnlyList<string>? _permissions;

    private HostConnection(string dataDirectory, string? clientName, IReadOnlyList<string>? permissions)
    {
        _dataDirectory = dataDirectory;
        _clientName = clientName;
        _permissions = permissions;
    }

    public HandshakeResponse Handshake =>
        _handshake ?? throw new InvalidOperationException("尚未完成握手");

    /// <summary>连接已存在的宿主并完成握手；未运行时抛 HostUnavailable。</summary>
    public static async Task<HostConnection> ConnectAsync(
        string dataDirectory,
        string? clientName,
        CancellationToken ct,
        IReadOnlyList<string>? permissions = null)
    {
        var resolved = DataDirectory.Resolve(dataDirectory);
        if (!resolved.IsValid)
        {
            throw new HostClientException(HostClientErrorCodes.ProtocolError, $"数据目录非法：{resolved.Error}");
        }

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: ChannelNames.PipeName(resolved.ComparisonKey!),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
            throw new HostClientException(HostClientErrorCodes.HostUnavailable, "宿主管道连接超时");
        }

        var client = new HostConnection(dataDirectory, clientName, permissions) { _pipe = pipe };
        try
        {
            await client.HandshakeAsync(clientName, ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>发送请求并返回信封结果。同步完成的操作直接返回 completed。
    /// 管道瞬断（EndOfStream/IOException）时重连一次并重发同请求：写操作的幂等由
    /// 宿主收据中间件保证（catalog 中写操作均要求幂等键），读操作天然可重试。</summary>
    public async Task<Envelope<JsonElement>> InvokeAsync(IpcRequest request, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        var retried = false;
        try
        {
            while (true)
            {
                var pipe = _pipe ?? throw new InvalidOperationException("客户端未连接");
                try
                {
                    return await InvokeOnceAsync(pipe, request, ct);
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException && !retried)
                {
                    // 连接可能已被对端关闭：重连一次重发同请求（同 requestId）。
                    retried = true;
                    await ReconnectAsync(_dataDirectory, _clientName, ct);
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<Envelope<JsonElement>> InvokeOnceAsync(
        NamedPipeClientStream pipe, IpcRequest request, CancellationToken ct)
    {
        await IpcFrame.WriteJsonAsync(pipe, request, ct);
        var envelope = await IpcFrame.ReadJsonAsync<Envelope<JsonElement>>(pipe, ct);
        if (!string.Equals(envelope.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new HostClientException(
                HostClientErrorCodes.ProtocolError,
                $"响应 requestId 不匹配：期望 {request.RequestId}，收到 {envelope.RequestId}");
        }

        return envelope;
    }

    /// <summary>断线后重连并重新握手；旧连接句柄被替换，重连失败时实例保持断开状态。</summary>
    public async Task ReconnectAsync(string dataDirectory, string? clientName, CancellationToken ct)
    {
        var oldPipe = _pipe;
        _pipe = null;
        _handshake = null;
        if (oldPipe is not null)
        {
            await oldPipe.DisposeAsync();
        }

        var reconnected = await ConnectAsync(dataDirectory, clientName, ct, _permissions);
        _pipe = reconnected._pipe;
        _handshake = reconnected._handshake;
        reconnected._pipe = null;
        await reconnected.DisposeAsync();
    }

    private async Task HandshakeAsync(string? clientName, CancellationToken ct)
    {
        var pipe = _pipe!;
        await IpcFrame.WriteJsonAsync(
            pipe,
            new HandshakeRequest { ClientName = clientName, Permissions = _permissions },
            ct);
        var response = await IpcFrame.ReadJsonAsync<HandshakeResponse>(pipe, ct);

        if (!string.Equals(response.ApiVersion, ApiConstants.ApiVersion, StringComparison.Ordinal))
        {
            throw new HostClientException(
                HostClientErrorCodes.HostVersionMismatch,
                $"宿主协议版本不兼容：客户端 {ApiConstants.ApiVersion}，宿主 {response.ApiVersion}");
        }

        _handshake = response;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handshake = null;
        var pipe = _pipe;
        _pipe = null;
        _sendLock.Dispose();
        if (pipe is not null)
        {
            await pipe.DisposeAsync();
        }
    }
}
