using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;

namespace GameLibrary.TauriBridge;

public sealed class BridgeServer
{
    /// <summary>普通请求与 events.wait 各用一条 HostConnection，两条 lane 可以同时执行。</summary>
    private sealed class Lane
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public HostConnection? Connection { get; set; }

        public string? DataDirectory { get; set; }

        public string? StopDataDirectory { get; set; }
    }

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _outputLock = new();
    private readonly Lane _ordinary = new();
    private readonly Lane _events = new();

    public BridgeServer(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task>();
        try
        {
            while (!runCancellation.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _input.ReadLineAsync(runCancellation.Token).AsTask().WaitAsync(runCancellation.Token);
                }
                catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                BridgeFrameRequest? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<BridgeFrameRequest>(line, ContractJson.Options);
                }
                catch (JsonException)
                {
                    // A corrupt private frame cannot be paired safely; EOF fails all Rust waiters.
                    break;
                }

                if (frame is null || string.IsNullOrWhiteSpace(frame.TransportId) || frame.Request is null)
                {
                    break;
                }

                // 读取循环只负责分派：events.wait 挂起时普通请求仍能被读入并执行。
                var lane = string.Equals(frame.Request.OperationId, "events.wait", StringComparison.Ordinal)
                    ? _events
                    : _ordinary;
                pending.Add(ProcessAsync(frame, lane, runCancellation.Token));
                pending.RemoveAll(task => task.IsCompleted);
            }
        }
        finally
        {
            // stdin EOF / 父进程退出：取消在途 lane 工作，避免长轮询拖住桥接进程的清理。
            runCancellation.Cancel();
            try
            {
                await Task.WhenAll(pending);
            }
            catch (Exception)
            {
                // 关闭路径优先完成连接清理；单个响应写入失败不应阻止退出。
            }

            await DiscardConnectionAsync(_ordinary);
            await DiscardConnectionAsync(_events);
        }
    }

    private async Task ProcessAsync(BridgeFrameRequest frame, Lane lane, CancellationToken cancellationToken)
    {
        try
        {
            await lane.Gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        BridgeFrameResponse response;
        try
        {
            try
            {
                var result = await ExecuteAsync(frame.Request, lane, cancellationToken);
                response = new BridgeFrameResponse
                {
                    TransportId = frame.TransportId,
                    Result = result,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var (code, message) = MapTransportError(ex);
                response = new BridgeFrameResponse
                {
                    TransportId = frame.TransportId,
                    TransportError = new BridgeTransportError { Code = code, Message = message },
                };
            }
        }
        finally
        {
            lane.Gate.Release();
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            WriteResponse(response);
        }
    }

    /// <summary>
    /// 整行写 stdout。两条 lane 的响应可能乱序完成，加锁保证每个 JSON 帧不会被字节级穿插；
    /// 响应归属由 transportId 决定，不依赖写入顺序。
    /// </summary>
    private void WriteResponse(BridgeFrameResponse response)
    {
        var line = JsonSerializer.Serialize(response, ContractJson.Options);
        lock (_outputLock)
        {
            try
            {
                _output.WriteLine(line);
                _output.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 父进程已关闭管道：让读取循环自行观察到 EOF 并退出。
            }
        }
    }

    private async Task<Envelope<JsonElement>> ExecuteAsync(
        BridgeRequest request,
        Lane lane,
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
            var connection = await EnsureConnectionAsync(lane, dataDirectory, cancellationToken);
            return await connection.InvokeAsync(ipcRequest, cancellationToken);
        }
        catch (Exception ex) when (IsConnectionFault(ex) && !cancellationToken.IsCancellationRequested)
        {
            // 本 lane 的连接已失效（宿主重启/崩溃/断线）：丢弃该连接、重建宿主后同一 requestId 重试一次。
            // 写操作的重复由宿主持久收据保证；非连接类错误不重试，原样上抛。
            await DiscardConnectionAsync(lane);
            var connection = await EnsureConnectionAsync(lane, dataDirectory, cancellationToken);
            return await connection.InvokeAsync(ipcRequest, cancellationToken);
        }
    }

    private async Task<HostConnection> EnsureConnectionAsync(
        Lane lane,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        if (lane.Connection is not null
            && string.Equals(lane.DataDirectory, dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return lane.Connection;
        }

        await DiscardConnectionAsync(lane);
        var connection = await HostProcessLauncher.EnsureStartedAsync(
            dataDirectory,
            clientName: "tauri",
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken,
            ClientPermissionProfiles.TauriBridge);
        lane.DataDirectory = dataDirectory;
        lane.StopDataDirectory = dataDirectory;
        lane.Connection = connection;
        return connection;
    }

    /// <summary>销毁指定 lane 的缓存连接并置空；清理期的 IO/释放异常不得中断请求路径或退出流程。</summary>
    private static async Task DiscardConnectionAsync(Lane lane)
    {
        var connection = lane.Connection;
        lane.Connection = null;
        lane.DataDirectory = null;
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

    private static (string Code, string Message) MapTransportError(Exception ex) => ex switch
    {
        HostClientException host => (host.Code, host.Message),
        JsonException or InvalidOperationException => (ErrorCodes.InvalidArgument, ex.Message),
        _ => (ErrorCodes.HostUnavailable, ex.Message),
    };

    /// <summary>Only stop the connected data scope; never start a Host merely to stop it.</summary>
    internal async Task StopHostAsync()
    {
        var dataDirectories = new[] { _ordinary.StopDataDirectory, _events.StopDataDirectory }
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // 先清理缓存连接：宿主会话即将结束，缓存的连接句柄不再可用。
        await DiscardConnectionAsync(_ordinary);
        await DiscardConnectionAsync(_events);
        foreach (var dataDirectory in dataDirectories)
        {
            await StopConnectedHostAsync(dataDirectory);
        }
    }

    private static async Task StopConnectedHostAsync(string dataDirectory)
    {
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
}
