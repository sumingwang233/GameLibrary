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

    public HandshakeResponse Handshake =>
        _handshake ?? throw new InvalidOperationException("尚未完成握手");

    /// <summary>连接已存在的宿主并完成握手；未运行时抛 HostUnavailable。</summary>
    public static async Task<HostConnection> ConnectAsync(string dataDirectory, string? clientName, CancellationToken ct)
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

        var client = new HostConnection { _pipe = pipe };
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

    /// <summary>发送请求并返回信封结果。同步完成的操作直接返回 completed。</summary>
    public async Task<Envelope<JsonElement>> InvokeAsync(IpcRequest request, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("客户端未连接");
        await _sendLock.WaitAsync(ct);
        try
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
        finally
        {
            _sendLock.Release();
        }
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

        var reconnected = await ConnectAsync(dataDirectory, clientName, ct);
        _pipe = reconnected._pipe;
        _handshake = reconnected._handshake;
        reconnected._pipe = null;
        await reconnected.DisposeAsync();
    }

    private async Task HandshakeAsync(string? clientName, CancellationToken ct)
    {
        var pipe = _pipe!;
        await IpcFrame.WriteJsonAsync(pipe, new HandshakeRequest { ClientName = clientName }, ct);
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
