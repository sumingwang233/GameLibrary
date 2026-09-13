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

    /// <summary>已接入收据的操作子集：catalog 声明 requiresIdempotencyKey 的已实现操作。
    /// scan.start 的作业收据随 T16（作业/收据同事务）接入，内存态作业先行。</summary>
    private static readonly HashSet<string> ReceiptOperations = new(StringComparer.Ordinal)
    {
        "launch.execute",
        "profiles.create",
        "profiles.update",
    };

    public Envelope<object> Dispatch(IpcRequest request)
    {
        var info = OperationCatalog.Catalog.Find(request.OperationId);
        if (info is { RequiresIdempotencyKey: true, IsAvailable: true }
            && ReceiptOperations.Contains(request.OperationId))
        {
            return DispatchWithReceipt(request);
        }

        return DispatchCore(request);
    }

    /// <summary>
    /// 幂等收据（契约 7.1）：同键同摘要重放返回原结果，不重复执行；同键不同摘要返回
    /// IdempotencyConflict。launch.execute 的 prepared 收据在进程已创建但结果未落时，
    /// 按 PID+启动时间+路径尽力核实，无法证明即 UnknownOutcome，原键重试不再启动。
    /// </summary>
    private Envelope<object> DispatchWithReceipt(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = "库未初始化（先 library.init）；幂等收据需要库实例",
                    Retryable = false,
                },
            };
        }

        if (!TryGetStringParameter(request, "idempotencyKey", out var key))
        {
            return InvalidArgument(request, $"{request.OperationId} 需要 idempotencyKey 参数");
        }

        var actor = string.IsNullOrWhiteSpace(request.ClientName) ? "anonymous" : request.ClientName!;
        var digest = RequestDigest(request);
        var existing = store.TryGetReceipt(actor, request.OperationId, key);
        if (existing is not null)
        {
            if (existing.RequestDigest != digest)
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.IdempotencyConflict,
                        Message = $"幂等键已被不同请求使用：{key}",
                        Retryable = false,
                    },
                };
            }

            if (existing.Status == "completed" && existing.ResultJson is not null)
            {
                var replayed = JsonSerializer.Deserialize<Envelope<object>>(existing.ResultJson, ContractJson.Options);
                if (replayed is not null)
                {
                    // 收据重放：结果内容不变，但 RequestId 必须对齐本次请求（客户端按其校验）。
                    return new Envelope<object>
                    {
                        ApiVersion = replayed.ApiVersion,
                        RequestId = request.RequestId,
                        LibraryInstanceId = replayed.LibraryInstanceId,
                        DataEpoch = replayed.DataEpoch,
                        Ok = replayed.Ok,
                        Status = replayed.Status,
                        Data = replayed.Data,
                        JobId = replayed.JobId,
                        Error = replayed.Error,
                        Warnings = replayed.Warnings,
                        NextActions = replayed.NextActions,
                    };
                }
            }

            if (request.OperationId == "launch.execute")
            {
                var recovery = RecoverLaunchReceipt(request, store, existing);
                if (recovery is not null)
                {
                    return recovery;
                }
            }
        }

        var receipt = existing ?? NewReceipt(store, actor, request, key, digest);
        if (existing is null)
        {
            store.InsertPreparedReceipt(receipt);
        }

        var result = DispatchCore(request);
        var resultJson = JsonSerializer.Serialize(result, ContractJson.Options);
        if (request.OperationId == "launch.execute" && result.Ok)
        {
            TryAttachAttemptRef(store, receipt, resultJson);
        }

        store.CompleteReceipt(receipt, resultJson);
        return result;
    }

    /// <summary>进程已创建的 launch 收据立即记录尝试引用：缩小"已启动未落收据"的崩溃歧义窗口。</summary>
    private static void TryAttachAttemptRef(
        Infrastructure.Persistence.SqliteLibraryStore store,
        Infrastructure.Persistence.RequestReceipt receipt,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var data = document.RootElement.GetProperty("data");
            if (data.GetProperty("state").GetString() != "processCreated")
            {
                return;
            }

            var attemptRef = new AttemptRef
            {
                AttemptId = data.GetProperty("attemptId").GetString() ?? "",
                ProcessId = data.GetProperty("processId").GetInt32(),
                ProcessStartedUtc = DateTime.Parse(
                    data.GetProperty("processStartedUtc").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                ExecutablePath = data.GetProperty("executablePath").GetString() ?? "",
            };
            store.UpdateReceiptAttempt(receipt, JsonSerializer.Serialize(attemptRef, ContractJson.Options));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // 尝试引用写不进去只是保留较宽的歧义窗口，不影响执行结果。
        }
    }

    /// <summary>
    /// prepared 收据恢复：有尝试引用则核实（本进程注册表优先，再按 PID/启动时间核实）；
    /// 核实成功返回尝试现状，无法证明返回 UnknownOutcome 并终结收据；无尝试引用
    /// （进程尚未创建即中断）返回 null，允许本次执行继续。
    /// </summary>
    private Envelope<object>? RecoverLaunchReceipt(IpcRequest request, Infrastructure.Persistence.SqliteLibraryStore store, Infrastructure.Persistence.RequestReceipt existing)
    {
        if (existing.AttemptJson is null)
        {
            return null;
        }

        AttemptRef? attemptRef;
        try
        {
            attemptRef = JsonSerializer.Deserialize<AttemptRef>(existing.AttemptJson, ContractJson.Options);
        }
        catch (JsonException)
        {
            attemptRef = null;
        }

        if (attemptRef is null)
        {
            return null;
        }

        var attempt = _state.Launches.GetAttempt(attemptRef.AttemptId);
        if (attempt is not null)
        {
            var live = new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = attempt.ToDto(),
            };
            store.CompleteReceipt(existing, JsonSerializer.Serialize(live, ContractJson.Options));
            return live;
        }

        if (IsProcessVerified(attemptRef))
        {
            // 进程仍在但注册表无记录（不应发生）：保守返回 UnknownOutcome，不重启。
        }

        var unknown = new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.UnknownOutcome,
                Message = $"上次执行结果无法证明（attempt {attemptRef.AttemptId}，pid {attemptRef.ProcessId}）；请检查运行状态后用新幂等键显式重试，本键不再启动",
                Retryable = false,
            },
        };
        store.CompleteReceipt(existing, JsonSerializer.Serialize(unknown, ContractJson.Options));
        return unknown;
    }

    private static bool IsProcessVerified(AttemptRef attemptRef)
    {
        try
        {
            using var process = Process.GetProcessById(attemptRef.ProcessId);
            process.Refresh();
            if (process.HasExited)
            {
                return false;
            }

            var drift = Math.Abs((process.StartTime.ToUniversalTime() - attemptRef.ProcessStartedUtc).TotalSeconds);
            return drift <= 5;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static Infrastructure.Persistence.RequestReceipt NewReceipt(
        Infrastructure.Persistence.SqliteLibraryStore store, string actor, IpcRequest request, string key, string digest) =>
        new()
        {
            LibraryInstanceId = store.Info.LibraryInstanceId,
            Actor = actor,
            OperationId = request.OperationId,
            IdempotencyKey = key,
            RequestDigest = digest,
            Status = "prepared",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

    private static string RequestDigest(IpcRequest request) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.Parameters?.GetRawText() ?? "")));

    private sealed record AttemptRef
    {
        public string AttemptId { get; init; } = "";

        public int ProcessId { get; init; }

        public DateTime ProcessStartedUtc { get; init; }

        public string ExecutablePath { get; init; } = "";
    }

    private Envelope<object> DispatchCore(IpcRequest request) => request.OperationId switch
    {
        "library.init" => LibraryInit(request),
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
        "roots.add" => RootsAdd(request),
        "roots.list" => RootsList(request),
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

        if (RejectPathOutsideRoots(request, rootPath.PhysicalPath) is { } outsideRoot)
        {
            return outsideRoot;
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

    /// <summary>注册库根（显式授权动作）；重复注册同一规范化路径幂等。</summary>
    private Envelope<object> RootsAdd(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "root", out var root))
        {
            return InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
        }

        try
        {
            var libraryRoot = _state.Roots.Add(root);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = libraryRoot.ToDto(),
            };
        }
        catch (Scanning.RootRegistryException ex)
        {
            return new Envelope<object>
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
        }
    }

    private Envelope<object> RootsList(IpcRequest request)
    {
        var roots = _state.Roots.List();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = roots.Count,
                items = roots.Select(r => r.ToDto()).ToArray(),
            },
        };
    }

    /// <summary>路径包含校验（CWE-22 边界）：调用方路径必须在已注册库根内。</summary>
    private Envelope<object>? RejectPathOutsideRoots(IpcRequest request, string physicalPath)
    {
        if (_state.Roots.Contains(physicalPath))
        {
            return null;
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.PermissionDenied,
                Message = $"路径不在已注册库根内（先通过 roots.add 注册）：{physicalPath}",
                Retryable = false,
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

        if (RejectPathOutsideRoots(request, validation.Path.PhysicalPath) is { } inspectOutsideRoot)
        {
            return inspectOutsideRoot;
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

        if (RejectPathOutsideRoots(request, executablePath) is { } createOutsideRoot)
        {
            return createOutsideRoot;
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

        if (RejectPathOutsideRoots(request, executablePath) is { } updateOutsideRoot)
        {
            return updateOutsideRoot;
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

    /// <summary>
    /// 显式建库（library.init）。自举豁免前置收据：建库成功后在新库中登记收据，
    /// 同键重放返回原结果；DB 已存在但收据缺失（建库后、收据前中断）返回 AlreadyInitialized。
    /// </summary>
    private Envelope<object> LibraryInit(IpcRequest request)
    {
        if (_state.Library.Store is not null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = "库已初始化，不能重复执行 library.init",
                    Retryable = false,
                },
            };
        }

        var identity = _state.Identity;
        var options = new Infrastructure.Persistence.SqliteLibraryStoreOptions
        {
            AppVersion = identity.AppVersion,
            ApiVersion = ApiConstants.ApiVersion,
        };

        // InitializeAsync 全部同步完成（本地 SQLite），分发器保持同步签名。
        var init = Infrastructure.Persistence.SqliteLibraryStore.InitializeAsync(_state.DataDirectory, options, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!init.IsOpened)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = $"建库失败（{init.Status}）：{init.Detail}",
                    Retryable = false,
                },
            };
        }

        _state.Library = new HostLibraryState
        {
            Status = init.Status,
            Store = init.Store,
            Detail = init.Detail,
        };

        var result = new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                libraryInstanceId = init.Store!.Info.LibraryInstanceId,
                dataEpoch = init.Store.Info.DataEpoch,
                schemaVersion = init.Store.Info.SchemaVersion,
            },
        };

        if (TryGetStringParameter(request, "idempotencyKey", out var key))
        {
            var actor = string.IsNullOrWhiteSpace(request.ClientName) ? "anonymous" : request.ClientName!;
            var receipt = new Infrastructure.Persistence.RequestReceipt
            {
                LibraryInstanceId = init.Store.Info.LibraryInstanceId,
                Actor = actor,
                OperationId = request.OperationId,
                IdempotencyKey = key,
                RequestDigest = RequestDigest(request),
                Status = "prepared",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            init.Store.InsertPreparedReceipt(receipt);
            init.Store.CompleteReceipt(receipt, JsonSerializer.Serialize(result, ContractJson.Options));
        }

        return result;
    }

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
