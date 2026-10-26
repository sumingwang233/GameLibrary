using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;

namespace GameLibrary.TauriBridge;

public sealed class BridgeServer
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private HostConnection? _connection;
    private string? _dataDirectory;

    public BridgeServer(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(cancellationToken).AsTask().WaitAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            object response;
            try
            {
                var request = JsonSerializer.Deserialize<BridgeRequest>(line, ContractJson.Options)
                    ?? throw new InvalidOperationException("桥接请求为空");
                response = await ExecuteAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or HostClientException)
            {
                response = new Envelope<object>
                {
                    RequestId = TryGetRequestId(line),
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ex is HostClientException
                            ? ErrorCodes.HostUnavailable
                            : ErrorCodes.InvalidArgument,
                        Message = ex.Message,
                        Retryable = ex is HostClientException,
                    },
                };
            }

            await _output.WriteLineAsync(JsonSerializer.Serialize(response, ContractJson.Options));
            await _output.FlushAsync(cancellationToken);
        }

        await DiscardConnectionAsync();
    }

    private async Task<Envelope<JsonElement>> ExecuteAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        var dataDirectory = ResolveDataDirectory(request.DataDir);
        // 重试必须复用同一个 requestId（宿主收据中间件靠它判重），所以请求只构造一次。
        var ipcRequest = new IpcRequest
        {
            RequestId = string.IsNullOrWhiteSpace(request.RequestId)
                ? $"tauri-{Guid.NewGuid():N}"
                : request.RequestId,
            OperationId = request.OperationId,
            Parameters = request.Parameters?.Clone(),
        };

        try
        {
            var connection = await EnsureConnectionAsync(dataDirectory, cancellationToken);
            return await connection.InvokeAsync(ipcRequest, cancellationToken);
        }
        catch (Exception ex) when (IsConnectionFault(ex) && !cancellationToken.IsCancellationRequested)
        {
            // 缓存的连接已失效（宿主重启/崩溃/断线）：丢弃缓存、重建宿主后同一 requestId 重试一次。
            // 写操作的重复由宿主持久收据保证；非连接类错误不重试，原样上抛。
            await DiscardConnectionAsync();
            var connection = await EnsureConnectionAsync(dataDirectory, cancellationToken);
            return await connection.InvokeAsync(ipcRequest, cancellationToken);
        }
    }

    /// <summary>取缓存连接；数据目录变化或缓存为空时用 EnsureStartedAsync 建立新连接。</summary>
    private async Task<HostConnection> EnsureConnectionAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        if (_connection is not null
            && string.Equals(_dataDirectory, dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return _connection;
        }

        await DiscardConnectionAsync();
        _dataDirectory = dataDirectory;
        _connection = await HostProcessLauncher.EnsureStartedAsync(
            dataDirectory,
            clientName: "tauri",
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken,
            ClientPermissionProfiles.TauriBridge);
        return _connection;
    }

    /// <summary>销毁缓存连接并置空；清理期的 IO/释放异常不得中断请求路径或退出流程。</summary>
    private async Task DiscardConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>仅这些异常表示「连接已不可用」；业务错误（协议错误、库实例不匹配等）不得重试。</summary>
    private static bool IsConnectionFault(Exception ex) => ex switch
    {
        HostClientException host => string.Equals(host.Code, HostClientErrorCodes.HostUnavailable, StringComparison.Ordinal),
        EndOfStreamException or IOException => true,
        InvalidOperationException invalid => invalid.Message.Contains("客户端未连接", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Only stop the connected data scope; never start a Host merely to stop it.</summary>
    internal async Task StopHostAsync()
    {
        var dataDirectory = _dataDirectory;
        // 先清理缓存连接：宿主会话即将结束，缓存的连接句柄不再可用。
        await DiscardConnectionAsync();
        if (dataDirectory is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = await HostConnection.ConnectAsync(
                dataDirectory,
                "tauri-exit",
                timeout.Token,
                ClientPermissionProfiles.HostExit);
            await connection.InvokeAsync(new IpcRequest
            {
                RequestId = "tauri-exit-" + Guid.NewGuid().ToString("N"),
                OperationId = "host.stop",
                Parameters = JsonSerializer.SerializeToElement(new { idempotencyKey = "tauri-exit-" + Guid.NewGuid().ToString("N") }, ContractJson.Options),
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is HostClientException or IOException or OperationCanceledException) { }
    }

    private static string ResolveDataDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }

        var configured = Environment.GetEnvironmentVariable("GAMELIBRARY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "GameLibrary");
    }

    private static string TryGetRequestId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("requestId", out var value)
                ? value.GetString() ?? $"tauri-error-{Guid.NewGuid():N}"
                : $"tauri-error-{Guid.NewGuid():N}";
        }
        catch (JsonException)
        {
            return $"tauri-error-{Guid.NewGuid():N}";
        }
    }
}
