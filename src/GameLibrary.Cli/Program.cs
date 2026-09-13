using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;

namespace GameLibrary.Cli;

/// <summary>
/// gamelibrary.exe：默认非交互；--format json 的 stdout 只输出一个结果 JSON，
/// 诊断写 stderr。退出码是公开契约（契约第 5 节）。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitArgumentError = 2;
    private const int ExitHostUnavailable = 7;
    private const int ExitBusinessFailed = 8;

    private static async Task<int> Main(string[] args)
    {
        var parse = CommandLine.Parse(args);
        if (!parse.IsValid)
        {
            Console.Error.WriteLine(parse.Error);
            return ExitArgumentError;
        }

        try
        {
            return parse.OperationId switch
            {
                "capabilities.get" => await CapabilitiesAsync(parse),
                "schema.get" => Schema(parse),
                "host.status" => await HostStatusAsync(parse),
                _ => UnknownCommand(parse),
            };
        }
        catch (HostClientException ex)
        {
            WriteEnvelope(new Envelope<object>
            {
                RequestId = parse.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ex.Code == HostClientErrorCodes.HostVersionMismatch
                        ? ErrorCodes.HostVersionMismatch
                        : ErrorCodes.HostUnavailable,
                    Message = ex.Message,
                    Retryable = true,
                },
            });
            return ExitHostUnavailable;
        }
    }

    private static int UnknownCommand(CommandLine cli)
    {
        Console.Error.WriteLine(
            $"命令未实现：{string.Join(' ', cli.Words)}（当前已实现：capabilities get / schema get / host status）");
        return ExitArgumentError;
    }

    /// <summary>capabilities 允许离线：宿主未连接时返回静态编译契约并标注 hostConnected=false。</summary>
    private static async Task<int> CapabilitiesAsync(CommandLine cli)
    {
        if (cli.DataDir is not null && TryConnect(cli, out var connection) && connection is not null)
        {
            try
            {
                var envelope = await connection.InvokeAsync(
                    new IpcRequest { RequestId = cli.RequestId, OperationId = "capabilities.get" },
                    CancellationToken.None);
                WriteEnvelope(envelope);
                return EnvelopeExitCode(envelope);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        var catalog = OperationCatalog.Catalog;
        WriteEnvelope(new Envelope<object>
        {
            RequestId = cli.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                apiVersion = catalog.ApiVersion,
                catalogVersion = catalog.CatalogVersion,
                hostConnected = false,
                availableOperations = catalog.AvailableOperations.Select(op => op.OperationId).ToArray(),
                plannedOperationsCount = catalog.Operations.Count - catalog.AvailableOperations.Count,
                permissions = catalog.Permissions,
            },
        });
        return ExitOk;
    }

    private static int Schema(CommandLine cli)
    {
        if (cli.OperationArgument is null)
        {
            Console.Error.WriteLine("schema get 需要 --operation <operationId>");
            return ExitArgumentError;
        }

        var info = OperationCatalog.Catalog.Find(cli.OperationArgument);
        if (info is null)
        {
            WriteEnvelope(new Envelope<object>
            {
                RequestId = cli.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = $"未知操作：{cli.OperationArgument}",
                    Retryable = false,
                },
            });
            return ExitArgumentError;
        }

        WriteEnvelope(new Envelope<object>
        {
            RequestId = cli.RequestId,
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
        });
        return ExitOk;
    }

    private static async Task<int> HostStatusAsync(CommandLine cli)
    {
        if (cli.DataDir is null)
        {
            Console.Error.WriteLine("host status 需要 --data-dir（或部署配置提供）");
            return ExitArgumentError;
        }

        if (cli.NoStart)
        {
            if (!TryConnect(cli, out var connection) || connection is null)
            {
                WriteEnvelope(new Envelope<object>
                {
                    RequestId = cli.RequestId,
                    Ok = true,
                    Status = OperationStatus.Completed,
                    Data = new { running = false },
                });
                return ExitOk;
            }

            try
            {
                var envelope = await connection.InvokeAsync(
                    new IpcRequest { RequestId = cli.RequestId, OperationId = "host.status" },
                    CancellationToken.None);
                WriteEnvelope(envelope);
                return EnvelopeExitCode(envelope);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        await using var started = await HostProcessLauncher.EnsureStartedAsync(
            cli.DataDir, clientName: "cli", timeout: TimeSpan.FromSeconds(cli.TimeoutSeconds));
        var response = await started.InvokeAsync(
            new IpcRequest { RequestId = cli.RequestId, OperationId = "host.status" },
            CancellationToken.None);
        WriteEnvelope(response);
        return EnvelopeExitCode(response);
    }

    private static bool TryConnect(CommandLine cli, out HostConnection? connection)
    {
        try
        {
            connection = HostConnection.ConnectAsync(cli.DataDir!, "cli", CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch (HostClientException ex) when (ex.Code == HostClientErrorCodes.HostUnavailable)
        {
            connection = null;
            return false;
        }
    }

    private static int EnvelopeExitCode(Envelope<JsonElement> envelope)
    {
        if (envelope.Ok && envelope.Status == OperationStatus.Completed)
        {
            return ExitOk;
        }

        return envelope.Error?.Code switch
        {
            ErrorCodes.InvalidArgument or ErrorCodes.UnsupportedPath or ErrorCodes.InvalidPath => ExitArgumentError,
            ErrorCodes.RevisionConflict or ErrorCodes.IdempotencyConflict => 4,
            ErrorCodes.PermissionDenied => 5,
            ErrorCodes.NeedsAuthorization => 6,
            ErrorCodes.HostUnavailable or ErrorCodes.HostVersionMismatch => ExitHostUnavailable,
            _ => ExitBusinessFailed,
        };
    }

    private static void WriteEnvelope<T>(Envelope<T> envelope)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, ContractJson.Options));
    }
}
