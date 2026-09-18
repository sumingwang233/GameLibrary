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
            var line = await _input.ReadLineAsync(cancellationToken);
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

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private async Task<Envelope<JsonElement>> ExecuteAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        var dataDirectory = ResolveDataDirectory(request.DataDir);
        if (_connection is null || !string.Equals(_dataDirectory, dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            _connection = await HostProcessLauncher.EnsureStartedAsync(
                dataDirectory,
                clientName: "tauri",
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);
            _dataDirectory = dataDirectory;
        }

        return await _connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? $"tauri-{Guid.NewGuid():N}"
                    : request.RequestId,
                OperationId = request.OperationId,
                Parameters = request.Parameters?.Clone(),
            },
            cancellationToken);
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
