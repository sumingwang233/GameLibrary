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
                "library.init" => await ScanHostOperationAsync(parse, "library.init", requiresRoot: false),
                "scan.start" => await ScanHostOperationAsync(parse, "scan.start", requiresRoot: true),
                "events.read" => await ScanHostOperationAsync(parse, "events.read", requiresRoot: false),
                "verification.start" or "verification.report" or "verification.invalidate" or "verification.get" or "verification.list" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "scan.inspect" => await ScanHostOperationAsync(parse, "scan.inspect", requiresRoot: true),
                "roots.add" => await ScanHostOperationAsync(parse, "roots.add", requiresRoot: true),
                "roots.list" => await ScanHostOperationAsync(parse, "roots.list", requiresRoot: false),
                "scan.status" or "scan.cancel" or "scan.coverage" or "jobs.get" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "candidates.list" or "candidates.get" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "candidates.accept" or "candidates.defer" or "candidates.ignore" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "games.list" or "games.get" or "games.update" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "translation.get" or "translation.set" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "diagnostics.status" or "diagnostics.logs" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "tools.discover" => await ScanHostOperationAsync(parse, "tools.discover", requiresRoot: true),
                "fields.set" => await ScanHostOperationAsync(parse, "fields.set", requiresRoot: false),
                "fields.clear" or "fields.reset" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "assets.import" => await ScanHostOperationAsync(parse, "assets.import", requiresRoot: false),
                "assets.choose" or "assets.crop" or "assets.reset" or "assets.remove" or "assets.list" or "assets.get" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "metadata.preview" or "metadata.refresh" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "ignores.list" or "ignores.create" or "ignores.remove" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "profiles.create" or "profiles.list" or "profiles.get" or "profiles.update"
                    or "profiles.set_default" or "profiles.remove" or "profiles.validate" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "views.list" or "views.get" or "views.create" or "views.update" or "views.remove" or "views.activate" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
                "launch.plan" or "launch.execute" or "launch.status" or "launch.history" =>
                    await ScanHostOperationAsync(parse, parse.OperationId, requiresRoot: false),
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
            $"命令未实现：{string.Join(' ', cli.Words)}（当前已实现：capabilities get / schema get / host status / scan start|status|cancel|coverage|inspect / candidates list|get / jobs get）");
        return ExitArgumentError;
    }

    /// <summary>宿主依赖的扫描/候选类操作：自动拉起宿主后单次调用。</summary>
    private static async Task<int> ScanHostOperationAsync(CommandLine cli, string operationId, bool requiresRoot)
    {
        if (cli.DataDir is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --data-dir（或部署配置提供）");
            return ExitArgumentError;
        }

        if (requiresRoot && cli.RootArgument is null)
        {
            Console.Error.WriteLine(operationId switch
            {
                "scan.inspect" => "scan inspect 需要 --path <绝对目录路径>",
                "roots.add" => "roots add 需要 --root <绝对路径>",
                _ => "scan start 需要 --root <绝对路径>",
            });
            return ExitArgumentError;
        }

        if (operationId == "candidates.get" && cli.CandidateId is null)
        {
            Console.Error.WriteLine("candidates get 需要 --candidate-id");
            return ExitArgumentError;
        }

        var requiresJobId = operationId is "scan.status" or "scan.cancel" or "scan.coverage" or "jobs.get";
        if (requiresJobId && cli.JobId is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --job-id");
            return ExitArgumentError;
        }

        if (operationId is "profiles.create" or "profiles.update" && cli.IdempotencyKey is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --idempotency-key");
            return ExitArgumentError;
        }

        if (operationId is "profiles.create")
        {
            if (cli.GameId is null || cli.ExePath is null || cli.Cwd is null)
            {
                Console.Error.WriteLine("profiles create 需要 --game-id、--exe、--cwd（argv 用 --arg 可重复提供）");
                return ExitArgumentError;
            }
        }

        if (operationId is "profiles.update"
            && (cli.ProfileId is null || cli.ExePath is null || cli.Cwd is null || cli.ExpectedRevision is null))
        {
            Console.Error.WriteLine("profiles update 需要 --profile-id、--exe、--cwd、--expected-revision（argv 用 --arg 可重复提供）");
            return ExitArgumentError;
        }

        if (operationId is "profiles.get" or "launch.plan" && cli.ProfileId is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --profile-id");
            return ExitArgumentError;
        }

        if (operationId is "launch.plan" or "profiles.create" && cli.GameId is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --game-id");
            return ExitArgumentError;
        }

        if (operationId is "launch.execute")
        {
            if (cli.IdempotencyKey is null)
            {
                Console.Error.WriteLine("launch execute 需要 --idempotency-key");
                return ExitArgumentError;
            }

            if (cli.PlanId is null && cli.ProfileId is null)
            {
                Console.Error.WriteLine("launch execute 需要 --plan-id 或 --profile-id");
                return ExitArgumentError;
            }
        }

        if (operationId is "launch.status" && cli.AttemptId is null)
        {
            Console.Error.WriteLine("launch status 需要 --attempt-id");
            return ExitArgumentError;
        }

        if (operationId is "candidates.accept" or "candidates.defer" or "candidates.ignore")
        {
            if (cli.CandidateId is null || cli.ExpectedRevision is null || cli.IdempotencyKey is null)
            {
                Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --candidate-id、--expected-revision、--idempotency-key");
                return ExitArgumentError;
            }
        }

        if (operationId is "games.get" && cli.GameId is null)
        {
            Console.Error.WriteLine("games get 需要 --game-id");
            return ExitArgumentError;
        }

        if (operationId is "translation.get" or "translation.set" or "games.update" && cli.GameId is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --game-id");
            return ExitArgumentError;
        }

        if (operationId is "translation.set"
            && (cli.Override is null || cli.ExpectedRevision is null))
        {
            Console.Error.WriteLine("translation set 需要 --override Auto|Required|NotRequired 和 --expected-revision");
            return ExitArgumentError;
        }

        if (operationId is "games.update"
            && (cli.Favorite is null || cli.ExpectedRevision is null))
        {
            Console.Error.WriteLine("games update 需要 --favorite 或 --unfavorite，以及 --expected-revision");
            return ExitArgumentError;
        }

        if (operationId is "profiles.set_default"
            && (cli.GameId is null || cli.ProfileId is null))
        {
            Console.Error.WriteLine("profiles set-default 需要 --game-id、--profile-id");
            return ExitArgumentError;
        }

        if (operationId is "profiles.remove" or "profiles.validate" && cli.ProfileId is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --profile-id");
            return ExitArgumentError;
        }

        if (operationId is "views.get" or "views.update" or "views.remove" or "views.activate" && cli.ViewId is null)
        {
            Console.Error.WriteLine($"{string.Join(' ', cli.Words)} 需要 --view-id");
            return ExitArgumentError;
        }

        if (operationId is "views.create" && cli.Name is null)
        {
            Console.Error.WriteLine("views create 需要 --name（--search/--favorite-only/--sort 可选）");
            return ExitArgumentError;
        }

        if (operationId is "views.update"
            && (cli.Name is null && cli.Search is null && !cli.FavoriteOnly && cli.Sort is null))
        {
            Console.Error.WriteLine("views update 至少提供 --name/--search/--favorite-only/--sort 之一");
            return ExitArgumentError;
        }

        if (operationId is "ignores.create"
            && (cli.RootArgument is null && cli.GameId is null))
        {
            Console.Error.WriteLine("ignores create 需要 --path（ExactPath/Subtree）或 --game-id（ConfirmedIdentity），scope 用 --scope");
            return ExitArgumentError;
        }

        if (operationId is "ignores.remove" && cli.CandidateId is null)
        {
            Console.Error.WriteLine("ignores remove 需要 --ignore-id");
            return ExitArgumentError;
        }

        await using var connection = await HostProcessLauncher.EnsureStartedAsync(
            cli.DataDir, clientName: "cli", timeout: TimeSpan.FromSeconds(cli.TimeoutSeconds));

        object? parameters = operationId switch
        {
            "library.init" => cli.IdempotencyKey is null ? null : new { idempotencyKey = cli.IdempotencyKey },
            "scan.start" => new { idempotencyKey = cli.IdempotencyKey ?? ("scan-" + Guid.NewGuid().ToString("N")), root = cli.RootArgument },
            "events.read" => (cli.Limit is null && cli.ExpectedRevision is null) ? null : new { cursor = cli.ExpectedRevision, limit = cli.Limit },
            "scan.inspect" => new { path = cli.RootArgument },
            "roots.add" => new { root = cli.RootArgument },
            "roots.list" => new { },
            "candidates.list" => cli.JobId is null ? null : new { jobId = cli.JobId },
            "candidates.get" => new { candidateId = cli.CandidateId },
            "candidates.accept" or "candidates.defer" or "candidates.ignore" => new
            {
                idempotencyKey = cli.IdempotencyKey,
                candidateId = cli.CandidateId,
                expectedRevision = cli.ExpectedRevision,
            },
            "games.list" => new { },
            "games.get" => new { gameId = cli.GameId },
            "games.update" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"gameupd-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                favorite = cli.Favorite,
                expectedRevision = cli.ExpectedRevision,
            },
            "translation.get" => new { gameId = cli.GameId },
            "translation.set" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"transset-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                @override = cli.Override,
                expectedRevision = cli.ExpectedRevision,
            },
            "diagnostics.status" => new { },
            "diagnostics.logs" => cli.Limit is null ? null : new { limit = cli.Limit },
            "tools.discover" => new { idempotencyKey = cli.IdempotencyKey ?? ("discover-" + Guid.NewGuid().ToString("N")), path = cli.RootArgument },
            "fields.set" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"field-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                field = cli.Field,
                value = cli.Value,
                expectedRevision = cli.ExpectedRevision,
            },
            "assets.import" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"import-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                sourcePath = cli.SourcePath,
            },
            "fields.clear" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"clear-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                field = cli.Field,
                expectedRevision = cli.ExpectedRevision,
            },
            "fields.reset" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"reset-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                field = cli.Field,
                expectedRevision = cli.ExpectedRevision,
            },
            "assets.choose" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"choose-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                assetId = cli.AssetId,
                expectedRevision = cli.ExpectedRevision,
            },
            "assets.crop" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"crop-{Guid.NewGuid():N}",
                assetId = cli.AssetId,
                x = cli.X,
                y = cli.Y,
                width = cli.Width,
                height = cli.Height,
            },
            "assets.reset" => new { gameId = cli.GameId },
            "assets.remove" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"rmasset-{Guid.NewGuid():N}",
                assetId = cli.AssetId,
            },
            "verification.start" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"vstart-{Guid.NewGuid():N}",
                toolId = cli.Field,
                fingerprint = cli.Value,
                engine = cli.Scope,
                samplePath = cli.SourcePath,
            },
            "verification.report" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"vreport-{Guid.NewGuid():N}",
                recordId = cli.CandidateId,
                gameStarted = cli.ExpectedRevision is not null && cli.ExpectedRevision >= 1,
                translationConfirmed = cli.ExpectedRevision is not null && cli.ExpectedRevision >= 2,
                fingerprint = cli.Value,
            },
            "verification.invalidate" => new { idempotencyKey = cli.IdempotencyKey ?? $"vinval-{Guid.NewGuid():N}", recordId = cli.CandidateId },
            "verification.get" => new { recordId = cli.CandidateId },
            "verification.list" => cli.Field is null ? null : new { toolId = cli.Field },
            "metadata.preview" => new { gameId = cli.GameId },
            "metadata.refresh" => new { idempotencyKey = cli.IdempotencyKey ?? $"meta-{Guid.NewGuid():N}", gameId = cli.GameId },
            "assets.list" => new { gameId = cli.GameId },
            "assets.get" => new { assetId = cli.CandidateId },
            "ignores.list" => new { },
            "ignores.create" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"ignore-{Guid.NewGuid():N}",
                scope = cli.Scope ?? "ExactPath",
                path = cli.RootArgument,
                gameId = cli.GameId,
                reason = cli.Reason,
            },
            "ignores.remove" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"unignore-{Guid.NewGuid():N}",
                ignoreId = cli.IgnoreId,
                expectedRevision = cli.ExpectedRevision,
            },
            "profiles.create" => new { idempotencyKey = cli.IdempotencyKey, gameId = cli.GameId, executablePath = cli.ExePath, argv = cli.ArgList, cwd = cli.Cwd, toolId = cli.ToolId, isDefault = cli.IsDefault },
            "profiles.set_default" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"setdef-{Guid.NewGuid():N}",
                gameId = cli.GameId,
                profileId = cli.ProfileId,
            },
            "profiles.remove" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"rmprof-{Guid.NewGuid():N}",
                profileId = cli.ProfileId,
            },
            "profiles.validate" => new { profileId = cli.ProfileId },
            "views.list" => new { },
            "views.get" => new { viewId = cli.ViewId },
            "views.create" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"viewnew-{Guid.NewGuid():N}",
                name = cli.Name,
                search = cli.Search,
                favoriteOnly = cli.FavoriteOnly,
                sort = cli.Sort,
            },
            "views.update" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"viewupd-{Guid.NewGuid():N}",
                viewId = cli.ViewId,
                name = cli.Name,
                search = cli.Search,
                favoriteOnly = cli.FavoriteOnly ? true : (bool?)null,
                sort = cli.Sort,
                expectedRevision = cli.ExpectedRevision,
            },
            "views.remove" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"viewrm-{Guid.NewGuid():N}",
                viewId = cli.ViewId,
                expectedRevision = cli.ExpectedRevision,
            },
            "views.activate" => new
            {
                idempotencyKey = cli.IdempotencyKey ?? $"viewact-{cli.ViewId}",
                viewId = cli.ViewId,
            },
            "profiles.update" => new
            {
                idempotencyKey = cli.IdempotencyKey,
                profileId = cli.ProfileId,
                executablePath = cli.ExePath,
                argv = cli.ArgList,
                cwd = cli.Cwd,
                expectedRevision = cli.ExpectedRevision,
            },
            "profiles.list" => cli.GameId is null ? null : new { gameId = cli.GameId },
            "profiles.get" => new { profileId = cli.ProfileId },
            "launch.plan" => new { gameId = cli.GameId, profileId = cli.ProfileId },
            "launch.execute" => cli.PlanId is null
                ? new { idempotencyKey = cli.IdempotencyKey, profileId = cli.ProfileId, expectedRevision = cli.ExpectedRevision }
                : new { idempotencyKey = cli.IdempotencyKey, planId = cli.PlanId },
            "launch.status" => new { attemptId = cli.AttemptId },
            "launch.history" => cli.GameId is null ? null : new { gameId = cli.GameId },
            _ => new { jobId = cli.JobId },
        };

        var envelope = await connection.InvokeAsync(
            new IpcRequest { RequestId = cli.RequestId, OperationId = operationId, Parameters = ToParameters(parameters) },
            CancellationToken.None);
        WriteEnvelope(envelope);
        return EnvelopeExitCode(envelope);
    }

    private static System.Text.Json.JsonElement? ToParameters(object? parameters)
    {
        if (parameters is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(parameters, ContractJson.Options);
        return JsonDocument.Parse(json).RootElement.Clone();
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
        if (envelope.Ok && envelope.Status is OperationStatus.Completed or OperationStatus.Accepted)
        {
            return ExitOk;
        }

        return envelope.Error?.Code switch
        {
            ErrorCodes.InvalidArgument or ErrorCodes.UnsupportedPath or ErrorCodes.InvalidPath => ExitArgumentError,
            ErrorCodes.NotFound => 3,
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
