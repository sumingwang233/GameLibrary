using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Scanning;

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

/// <summary>T21/T10 骨架分发器：host.status / capabilities.get / schema.get 可用；其余返回 UnsupportedOperation。</summary>
public sealed class OperationDispatcher
{
    private readonly HostRuntimeState _state;

    public OperationDispatcher(HostRuntimeState state)
    {
        _state = state;
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
                    hostInstanceId = _state.Identity.InstanceId,
                    processId = _state.Identity.ProcessId,
                    startedAtUtc = _state.Identity.StartedAtUtc.ToString("O"),
                    appVersion = _state.Identity.AppVersion,
                    apiVersion = ApiConstants.ApiVersion,
                    libraryInitialized = _state.Library.Initialized,
                    libraryState = _state.Library.Status.ToString(),
                    libraryInstanceId = _state.Library.LibraryInstanceId,
                    dataEpoch = _state.Library.DataEpoch,
                    schemaVersion = _state.Library.SchemaVersion,
                },
            },
            "capabilities.get" => new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = BuildCapabilities(),
            },
            "schema.get" => BuildSchema(request),
            "scan.start" => ScanStart(request),
            "scan.status" or "jobs.get" => JobSnapshotEnvelope(request, request.OperationId == "scan.status" ? "scan" : null),
            "scan.cancel" => ScanCancel(request),
            "scan.coverage" => ScanCoverage(request),
            "scan.inspect" => ScanInspect(request),
            "candidates.list" => CandidatesList(request),
            "candidates.get" => CandidatesGet(request),
            "profiles.create" => ProfilesCreate(request),
            "profiles.list" => ProfilesList(request),
            "profiles.get" => ProfilesGet(request),
            "profiles.update" => ProfilesUpdate(request),
            "launch.plan" => LaunchPlanHandler(request),
            "launch.execute" => LaunchExecute(request),
            "launch.status" => LaunchStatus(request),
            "launch.history" => LaunchHistory(request),
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

    private Envelope<object> ScanStart(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "root", out var root))
        {
            return InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(root);
        if (!validation.IsValid)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = validation.IsUnsupported ? ErrorCodes.UnsupportedPath : ErrorCodes.InvalidPath,
                    Message = $"根路径非法（{validation.Reason}）：{root}",
                    Retryable = false,
                },
            };
        }

        var rootPath = validation.Path!;
        if (!Directory.Exists(rootPath.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"根路径不存在或离线：{rootPath.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        var jobId = _state.Jobs.Create(
            "scan",
            context => Task.FromResult(ScanJobRunner.Run(
                rootPath,
                context,
                new ScanCandidateCollector(rootPath, context.JobId, _state.Candidates))));

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, kind = "scan", state = "running" },
        };
    }

    private Envelope<object> JobSnapshotEnvelope(IpcRequest request, string? expectedKind)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        var snapshot = _state.Jobs.Get(jobId);
        if (snapshot is null)
        {
            return NotFound(request, $"作业不存在：{jobId}");
        }

        if (expectedKind is not null && !string.Equals(snapshot.Kind, expectedKind, StringComparison.Ordinal))
        {
            return InvalidArgument(request, $"作业 {jobId} 类型是 {snapshot.Kind}，不是 {expectedKind}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                jobId = snapshot.JobId,
                kind = snapshot.Kind,
                state = snapshot.State,
                createdUtc = snapshot.CreatedUtc.ToString("O"),
                startedUtc = snapshot.StartedUtc?.ToString("O"),
                finishedUtc = snapshot.FinishedUtc?.ToString("O"),
                error = snapshot.Error,
            },
        };
    }

    private Envelope<object> ScanCancel(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        if (!_state.Jobs.RequestCancel(jobId))
        {
            return _state.Jobs.Get(jobId) is null
                ? NotFound(request, $"作业不存在：{jobId}")
                : InvalidArgument(request, $"作业已进入终态，无法取消：{jobId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { jobId, state = "cancelRequested" },
        };
    }

    private Envelope<object> ScanCoverage(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        var progress = _state.Jobs.TryGetProgress(jobId);
        if (progress is null)
        {
            return NotFound(request, $"作业不存在：{jobId}");
        }

        var (state, data) = progress.Value;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                jobId,
                state,
                coverage = data,
            },
        };
    }

    /// <summary>只读单路径识别（契约 scan.inspect）：不落候选、不启动作业。</summary>
    private Envelope<object> ScanInspect(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "path", out var path))
        {
            return InvalidArgument(request, "缺少 path 参数（绝对本地目录路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(path);
        if (!validation.IsValid)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = validation.IsUnsupported ? ErrorCodes.UnsupportedPath : ErrorCodes.InvalidPath,
                    Message = $"路径非法（{validation.Reason}）：{path}",
                    Retryable = false,
                },
            };
        }

        if (!Directory.Exists(validation.Path!.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"路径不存在或离线：{validation.Path.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        var snapshot = new FileSystemDirectorySnapshot(validation.Path);
        var report = new EngineDetectorSet(DefaultDetectors()).DetectAll(snapshot);
        var confirmed = report.Results
            .Where(r => r.Confidence >= DetectionConfidence.Medium)
            .ToArray();

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                path = validation.Path.PhysicalPath,
                recognized = confirmed.Length > 0,
                engines = confirmed.Select(r => new
                {
                    engine = r.Engine,
                    detectorVersion = r.DetectorVersion,
                    confidence = r.Confidence,
                    likelyRoots = r.LikelyRootRelativePaths,
                    entryCandidates = r.EntryCandidates.Select(e => new
                    {
                        relativePath = e.RelativePath,
                        score = e.Score,
                        reasons = e.Reasons,
                    }).ToArray(),
                }).ToArray(),
                engineConflict = report.Conflict is not null,
                evidence = report.Results.SelectMany(r => r.Evidence).Select(e => new
                {
                    ruleId = e.RuleId,
                    relativePath = e.RelativePath,
                    observation = e.Observation,
                    polarity = e.Polarity,
                    detail = e.Detail,
                }).ToArray(),
            },
        };
    }

    private Envelope<object> CandidatesList(IpcRequest request)
    {
        string? jobId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } listParameters
            && listParameters.TryGetProperty("jobId", out var jobElement)
            && jobElement.ValueKind == JsonValueKind.String)
        {
            jobId = jobElement.GetString();
        }

        var candidates = _state.Candidates.List(jobId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = candidates.Count,
                items = candidates.Select(c => c.ToListItem()).ToArray(),
            },
        };
    }

    private Envelope<object> CandidatesGet(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "candidateId", out var candidateId))
        {
            return InvalidArgument(request, "缺少 candidateId 参数");
        }

        var candidate = _state.Candidates.Get(candidateId);
        if (candidate is null)
        {
            return NotFound(request, $"候选不存在：{candidateId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = candidate.ToDetail(),
        };
    }

    private Envelope<object> ProfilesCreate(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "executablePath", out var executablePath)
            || !TryGetStringParameter(request, "cwd", out var cwd))
        {
            return InvalidArgument(request, "profiles.create 需要 gameId、executablePath、cwd 参数");
        }

        if (!TryGetStringListParameter(request, "argv", out var argv))
        {
            return InvalidArgument(request, "profiles.create 需要 argv 字符串数组");
        }

        if (!File.Exists(executablePath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ToolMissing,
                    Message = $"启动目标不存在：{executablePath}",
                    Retryable = false,
                },
            };
        }

        if (!Directory.Exists(cwd))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidPath,
                    Message = $"工作目录不存在：{cwd}",
                    Retryable = false,
                },
            };
        }

        var profile = _state.Launches.AddProfile(gameId, executablePath, argv, cwd);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(profile),
        };
    }

    private Envelope<object> ProfilesList(IpcRequest request)
    {
        string? gameId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } profileListParameters
            && profileListParameters.TryGetProperty("gameId", out var gameElement)
            && gameElement.ValueKind == JsonValueKind.String)
        {
            gameId = gameElement.GetString();
        }

        var profiles = _state.Launches.ListProfiles(gameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = profiles.Count,
                items = profiles.Select(ProfileDto).ToArray(),
            },
        };
    }

    private Envelope<object> ProfilesGet(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "缺少 profileId 参数");
        }

        var profile = _state.Launches.GetProfile(profileId);
        if (profile is null)
        {
            return NotFound(request, $"Profile 不存在：{profileId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(profile),
        };
    }

    private Envelope<object> ProfilesUpdate(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId)
            || !TryGetStringParameter(request, "executablePath", out var executablePath)
            || !TryGetStringParameter(request, "cwd", out var cwd)
            || !TryGetStringListParameter(request, "argv", out var argv))
        {
            return InvalidArgument(request, "profiles.update 需要 profileId、executablePath、argv、cwd 参数");
        }

        TryGetIntParameter(request, "expectedRevision", out var expectedRevision);
        var current = _state.Launches.GetProfile(profileId);
        if (current is null)
        {
            return NotFound(request, $"Profile 不存在：{profileId}");
        }

        if (expectedRevision is not null && expectedRevision.Value != current.Revision)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"Profile Revision 不一致：期望 {expectedRevision}，当前 {current.Revision}",
                    Retryable = false,
                },
            };
        }

        if (!File.Exists(executablePath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ToolMissing,
                    Message = $"启动目标不存在：{executablePath}",
                    Retryable = false,
                },
            };
        }

        var updated = _state.Launches.UpdateProfile(profileId, executablePath, argv, cwd);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(updated),
        };
    }

    private Envelope<object> LaunchPlanHandler(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "launch.plan 需要 gameId、profileId 参数");
        }

        try
        {
            var plan = _state.Launches.CreatePlan(gameId, profileId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = plan.ToDto(),
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    private Envelope<object> LaunchExecute(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "idempotencyKey", out var idempotencyKey))
        {
            return InvalidArgument(request, "launch.execute 需要 idempotencyKey 参数");
        }

        TryGetStringParameter(request, "planId", out var planId);
        TryGetStringParameter(request, "profileId", out var profileId);
        TryGetIntParameter(request, "expectedRevision", out var expectedRevision);

        try
        {
            var attempt = _state.Launches.Execute(
                idempotencyKey,
                planId.Length > 0 ? planId : null,
                profileId.Length > 0 ? profileId : null,
                profileId.Length > 0 ? profileId : null,
                expectedRevision);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = attempt.ToDto(),
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    private Envelope<object> LaunchStatus(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "attemptId", out var attemptId))
        {
            return InvalidArgument(request, "缺少 attemptId 参数");
        }

        var attempt = _state.Launches.GetAttempt(attemptId);
        if (attempt is null)
        {
            return NotFound(request, $"启动尝试不存在：{attemptId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = attempt.ToDto(),
        };
    }

    private Envelope<object> LaunchHistory(IpcRequest request)
    {
        string? gameId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } historyParameters
            && historyParameters.TryGetProperty("gameId", out var historyGameElement)
            && historyGameElement.ValueKind == JsonValueKind.String)
        {
            gameId = historyGameElement.GetString();
        }

        var attempts = _state.Launches.History(gameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = attempts.Count,
                items = attempts.Select(a => a.ToDto()).ToArray(),
            },
        };
    }

    private static object ProfileDto(GameLibrary.Host.Launching.LaunchProfile profile) => new
    {
        profileId = profile.ProfileId,
        gameId = profile.GameId,
        executablePath = profile.ExecutablePath,
        argv = profile.Arguments,
        cwd = profile.WorkingDirectory,
        revision = profile.Revision,
    };

    private static Envelope<object> LaunchError(IpcRequest request, GameLibrary.Host.Launching.LaunchException ex) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ex.Code,
                Message = ex.Message,
                Retryable = false,
            },
        };

    private static IEngineDetector[] DefaultDetectors() =>
    [
        new UnityDetector(),
        new RpgMakerMvMzDetector(),
        new RenpyDetector(),
        new KirikiriDetector(),
        new FlashDetector(),
    ];

    private static bool TryGetStringParameter(IpcRequest request, string name, out string value)
    {
        value = "";
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            return value.Length > 0;
        }

        return false;
    }

    private static bool TryGetStringListParameter(IpcRequest request, string name, out IReadOnlyList<string> values)
    {
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    values = [];
                    return false;
                }

                list.Add(item.GetString() ?? "");
            }

            values = list;
            return true;
        }

        values = [];
        return false;
    }

    private static bool TryGetIntParameter(IpcRequest request, string name, out int? value)
    {
        value = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static Envelope<object> NotFound(IpcRequest request, string message) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.NotFound,
                Message = message,
                Retryable = false,
            },
        };

    private static object BuildCapabilities()
    {
        var catalog = OperationCatalog.Catalog;
        return new
        {
            apiVersion = catalog.ApiVersion,
            catalogVersion = catalog.CatalogVersion,
            appVersion = _identityAppVersion.Value,
            availableOperations = catalog.AvailableOperations.Select(op => op.OperationId).ToArray(),
            availableToolDetails = catalog.AvailableOperations.Select(op => new
            {
                operationId = op.OperationId,
                mcpTool = op.McpTool,
                cli = op.Cli,
                permission = op.Permission,
            }).ToArray(),
            plannedOperationsCount = catalog.Operations.Count - catalog.AvailableOperations.Count,
            permissions = catalog.Permissions,
            supportsCancellation = false,
            supportsEventSubscription = false,
        };
    }

    private static readonly Lazy<string> _identityAppVersion = new(() =>
        typeof(OperationDispatcher).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0");

    private static Envelope<object> BuildSchema(IpcRequest request)
    {
        string? operationId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty("operationId", out var operationElement)
            && operationElement.ValueKind == JsonValueKind.String)
        {
            operationId = operationElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            return InvalidArgument(request, "缺少 operationId 参数");
        }

        var info = OperationCatalog.Catalog.Find(operationId);
        if (info is null)
        {
            return InvalidArgument(request, $"未知操作：{operationId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
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
        };
    }

    private static Envelope<object> InvalidArgument(IpcRequest request, string message) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.InvalidArgument,
                Message = message,
                Retryable = false,
            },
        };
}
