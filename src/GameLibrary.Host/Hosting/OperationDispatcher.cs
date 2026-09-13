using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Host.Observability;
using GameLibrary.Host.Scanning;
using GameLibrary.Host.Tools;
using GameLibrary.Infrastructure.Persistence;
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
        "profiles.set_default",
        "profiles.remove",
        "translation.set",
        "games.update",
        "scan.start",
        "candidates.accept",
        "candidates.defer",
        "candidates.ignore",
        "fields.set",
        "verification.start",
        "verification.report",
        "verification.invalidate",
        "fields.clear",
        "fields.reset",
        "assets.import",
        "assets.choose",
        "assets.crop",
        "assets.reset",
        "assets.remove",
        "metadata.refresh",
        "ignores.create",
        "ignores.remove",
    };

    public Envelope<object> Dispatch(IpcRequest request)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = DispatchInternal(request);
        started.Stop();

        // 业务审计（T24，补充规格 4.3）：固定字段；参数原文不写入，actor 来自握手回填。
        _state.AuditLog.Append(new Observability.AuditRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Level = result.Ok ? "information" : "warning",
            Component = "dispatcher",
            OperationId = request.OperationId,
            RequestId = request.RequestId,
            Actor = LogSanitizer.Sanitize(string.IsNullOrWhiteSpace(request.ClientName) ? "anonymous" : request.ClientName, _state.DataDirectory),
            JobId = result.JobId,
            GameId = TryGetStringParameter(request, "gameId", out var auditGameId) ? auditGameId : null,
            ProfileId = TryGetStringParameter(request, "profileId", out var auditProfileId) ? auditProfileId : null,
            RuleId = TryGetStringParameter(request, "ignoreId", out var auditRuleId) ? auditRuleId : null,
            ResultCode = result.Ok ? result.Status.ToString() : result.Error?.Code ?? "Unknown",
            DurationMs = started.ElapsedMilliseconds,
        });
        return result;
    }

    private Envelope<object> DispatchInternal(IpcRequest request)
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
        "candidates.accept" => CandidateReview(request, "accept"),
        "candidates.defer" => CandidateReview(request, "defer"),
        "candidates.ignore" => CandidateReview(request, "ignore"),
        "games.list" => GamesList(request),
        "games.get" => GamesGet(request),
        "events.read" => EventsRead(request),
        "diagnostics.status" => DiagnosticsStatus(request),
        "diagnostics.logs" => DiagnosticsLogs(request),
        "tools.discover" => ToolsDiscover(request),
        "verification.start" => VerificationStart(request),
        "verification.report" => VerificationReport(request),
        "verification.invalidate" => VerificationInvalidate(request),
        "verification.get" => VerificationGet(request),
        "verification.list" => VerificationList(request),
        "fields.set" => FieldsSet(request),
        "fields.clear" => FieldsClear(request),
        "fields.reset" => FieldsReset(request),
        "assets.import" => AssetsImport(request),
        "assets.list" => AssetsList(request),
        "assets.get" => AssetsGet(request),
        "assets.choose" => AssetsChoose(request),
        "assets.crop" => AssetsCrop(request),
        "assets.reset" => AssetsReset(request),
        "assets.remove" => AssetsRemove(request),
        "metadata.preview" => MetadataPreview(request),
        "metadata.refresh" => MetadataRefresh(request),
        "ignores.list" => IgnoresList(request),
        "ignores.create" => IgnoresCreate(request),
        "ignores.remove" => IgnoresRemove(request),
        "profiles.create" => ProfilesCreate(request),
        "profiles.list" => ProfilesList(request),
        "profiles.get" => ProfilesGet(request),
        "profiles.update" => ProfilesUpdate(request),
        "profiles.set_default" => ProfilesSetDefault(request),
        "profiles.remove" => ProfilesRemove(request),
        "profiles.validate" => ProfilesValidate(request),
        "translation.get" => TranslationGet(request),
        "translation.set" => TranslationSet(request),
        "games.update" => GamesUpdate(request),
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
            context =>
            {
                _state.Coordinator.ManualScanRunning = true;
                try
                {
                    var collector = new ScanCandidateCollector(rootPath, context.JobId, _state.Candidates);
                    var outcome = ScanJobRunner.Run(rootPath, context, collector);
                    if (outcome.FinalState == "succeeded")
                    {
                        ScanCandidatePersistence.Persist(_state.Library.Store, _state.Events, collector, context.JobId);
                        _state.Events.Publish("scan.completed", $"job:{context.JobId}", new
                        {
                            jobId = context.JobId,
                            kind = "manual",
                            root = rootPath.PhysicalPath,
                        }, DateTime.UtcNow);
                    }

                    return Task.FromResult(outcome);
                }
                finally
                {
                    _state.Coordinator.ManualScanRunning = false;
                }
            });

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

        // T11 起以库内候选为事实来源（重扫刷新、审核状态演进）；无库时退回内存注册表。
        var store = _state.Library.Store;
        if (store is not null)
        {
            var persisted = store.ListCandidates()
                .Where(c => jobId is null || string.Equals(c.JobId, jobId, StringComparison.Ordinal))
                .Select(c => new
                {
                    candidateId = c.CandidateId,
                    jobId = c.JobId,
                    kind = c.Kind,
                    relativePath = c.RelativePath,
                    physicalPath = c.PhysicalPath,
                    reviewState = c.ReviewState,
                    revision = c.Revision,
                    gameId = c.GameId,
                    observedUtc = c.ObservedUtc.ToString("O"),
                })
                .ToArray();
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new { total = persisted.Length, items = persisted },
            };
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

        var store = _state.Library.Store;
        if (store is not null)
        {
            var persisted = store.TryGetCandidate(candidateId);
            if (persisted is null)
            {
                return NotFound(request, $"候选不存在：{candidateId}");
            }

            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    candidateId = persisted.CandidateId,
                    jobId = persisted.JobId,
                    kind = persisted.Kind,
                    relativePath = persisted.RelativePath,
                    physicalPath = persisted.PhysicalPath,
                    reviewState = persisted.ReviewState,
                    revision = persisted.Revision,
                    gameId = persisted.GameId,
                    detail = JsonSerializer.Deserialize<JsonElement>(persisted.PayloadJson, ContractJson.Options).Clone(),
                    observedUtc = persisted.ObservedUtc.ToString("O"),
                    updatedUtc = persisted.UpdatedUtc.ToString("O"),
                },
            };
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

    /// <summary>
    /// 审核（accept/defer/ignore）：仅 PendingReview 可转移（Deferred 需先重新查看）；
    /// accept 按路径幂等返回既有 GameId；ignore 同时登记 ExactPath 忽略规则。
    /// </summary>
    private Envelope<object> CandidateReview(IpcRequest request, string action)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）；候选审核需要库实例");
        }

        if (!TryGetStringParameter(request, "candidateId", out var candidateId))
        {
            return InvalidArgument(request, "缺少 candidateId 参数");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数（以 candidates.get 的 revision 为准）");
        }

        var current = store.TryGetCandidate(candidateId);
        if (current is null)
        {
            return NotFound(request, $"候选不存在：{candidateId}");
        }

        if (action == "accept" && current.ReviewState == "accepted" && current.GameId is not null)
        {
            // 幂等：重试同候选返回已有 GameId，不重复建卡。
            return CandidateReviewResult(request, current.ReviewState, current.Revision, current.GameId, null);
        }

        if (current.ReviewState != "pendingReview")
        {
            return InvalidArgument(request, $"候选当前状态 {current.ReviewState}；仅 pendingReview 可执行 {action}（重扫可将 observed 晋升）");
        }

        var utcNow = DateTime.UtcNow;
        string? gameId = null;
        string? ignoreId = null;
        if (action == "accept")
        {
            var existingGame = store.TryGetGameByRootPath(current.PhysicalPath);
            if (existingGame is not null)
            {
                gameId = existingGame.GameId;
            }
            else
            {
                gameId = $"game-{Guid.NewGuid():N}";
                store.InsertGame(new GameCard
                {
                    GameId = gameId,
                    Title = TitleFromPath(current.PhysicalPath, current.RelativePath),
                    RootPath = current.PhysicalPath,
                    Kind = current.Kind,
                    Engine = TopEngine(current.PayloadJson),
                    EntryPath = TopEntry(current.PayloadJson),
                    Membership = "active",
                    TranslationInherited = RequiredByToolNeed(current.PayloadJson),
                    AcceptedUtc = utcNow,
                    UpdatedUtc = utcNow,
                });
            }
        }

        if (action == "ignore")
        {
            ignoreId = $"ignore-{Guid.NewGuid():N}";
            store.InsertIgnoreRule(new IgnoreRule
            {
                IgnoreId = ignoreId,
                Scope = "ExactPath",
                Path = current.PhysicalPath,
                Reason = "candidates.ignore",
                CreatedUtc = utcNow,
            });
        }

        var updated = store.TransitionCandidate(
 candidateId, "pendingReview",
            action switch
            {
                "accept" => "accepted",
                "defer" => "deferred",
                _ => "ignored",
            },
            expectedRevision.Value,
            gameId,
            utcNow);
        if (updated is null)
        {
            var latest = store.TryGetCandidate(candidateId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"候选 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                },
            };
        }

        if (action == "accept" && updated.GameId is not null)
        {
            _state.Events.Publish("game.created", $"game:{updated.GameId}", new
            {
                gameId = updated.GameId,
                fromCandidate = updated.CandidateId,
                title = TitleFromPath(updated.PhysicalPath, updated.RelativePath),
            }, DateTime.UtcNow);
        }

        return CandidateReviewResult(request, updated.ReviewState, updated.Revision, updated.GameId, ignoreId);
    }

    private static Envelope<object> CandidateReviewResult(IpcRequest request, string state, int revision, string? gameId, string? ignoreId) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                reviewState = state,
                revision,
                gameId,
                ignoreId,
            },
        };

    private static string ToCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string TitleFromPath(string physicalPath, string relativePath) =>
        Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar))
        ?? (relativePath.Length > 0 ? relativePath.Split('/')[^1] : physicalPath);

    private static string? TopEngine(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var engines = document.RootElement.GetProperty("engines");
            if (engines.GetArrayLength() == 0)
            {
                return null;
            }

            return engines[0].GetProperty("engine").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TopEntry(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var entries = document.RootElement.GetProperty("entryCandidates");
            return entries.GetArrayLength() == 0
                ? null
                : entries[0].GetProperty("relativePath").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>从候选 payload 读取祖先 [toolNeed] 继承标记（accept 时落库到 games.translation_inherited）。</summary>
    private static bool RequiredByToolNeed(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement
                .GetProperty("classification")
                .GetProperty("requiredByToolNeed")
                .GetBoolean();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private Envelope<object> GamesList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var games = store.ListGames();
        string? search = null;
        bool? favoriteFilter = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } glParameters)
        {
            if (glParameters.TryGetProperty("search", out var searchElement) && searchElement.ValueKind == JsonValueKind.String)
            {
                search = searchElement.GetString();
            }

            if (glParameters.TryGetProperty("favorite", out var favElement) && favElement.ValueKind == JsonValueKind.True)
            {
                favoriteFilter = true;
            }
        }

        var filtered = games.AsEnumerable();
        if (favoriteFilter == true)
        {
            filtered = filtered.Where(g => g.Favorite);
        }

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(g => g.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var dtos = filtered.Select(g => GameDto(store, g)).ToArray();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = dtos.Length, items = dtos },
        };
    }

    private Envelope<object> GamesGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = GameDto(store, game),
        };
    }

    /// <summary>
    /// games.update（T13 补齐）：受限字段 patch。本步仅开放 favorite；
    /// 参数中出现任何未声明字段一律拒绝（契约 4：写请求只允许 schema 声明的字段）。
    /// </summary>
    private Envelope<object> GamesUpdate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (request.Parameters is not { ValueKind: JsonValueKind.Object } parameters)
        {
            return InvalidArgument(request, "缺少 patch 字段");
        }

        var declared = new HashSet<string>(StringComparer.Ordinal)
            { "gameId", "expectedRevision", "idempotencyKey", "favorite" };
        var unknown = parameters.EnumerateObject()
            .Where(p => !declared.Contains(p.Name))
            .Select(p => p.Name)
            .ToArray();
        if (unknown.Length > 0)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = $"未知 patch 字段：{string.Join(", ", unknown)}；games.update 当前仅支持 favorite",
                    Retryable = false,
                },
            };
        }

        if (!parameters.TryGetProperty("favorite", out var favoriteElement)
            || favoriteElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return InvalidArgument(request, "缺少 favorite 布尔字段（games.update 当前仅支持 favorite patch）");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var newRevision = store.SetFavorite(gameId, favoriteElement.ValueKind == JsonValueKind.True, expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            var latest = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                    CurrentRevision = latest?.Revision,
                },
            };
        }

        var updatedGame = store.TryGetGame(gameId)!;
        _state.Events.Publish("game.updated", $"game:{gameId}", new { gameId, revision = newRevision }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, favorite = updatedGame.Favorite, revision = newRevision.Value },
        };
    }

    /// <summary>translation.get：继承值与用户覆盖分离返回；有效值 = 覆盖优先（策划案 7.4）。</summary>
    private Envelope<object> TranslationGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TranslationDto(game),
        };
    }

    /// <summary>
    /// translation.set：只写用户覆盖层（Auto/Required/NotRequired），继承值不动；
    /// Required 不因覆盖缺失而回退为直启（回退需显式 NotRequired）。
    /// </summary>
    private Envelope<object> TranslationSet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        if (!TryGetStringParameter(request, "override", out var overrideText)
            || !Enum.TryParse<TranslationRequirement>(overrideText, ignoreCase: false, out var overrideValue))
        {
            return InvalidArgument(request, "override 必须是 Auto/Required/NotRequired（区分大小写）");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var storedOverride = overrideValue == TranslationRequirement.Auto ? null : overrideValue.ToString();
        var newRevision = store.SetTranslationOverride(gameId, storedOverride, expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            var latest = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                    CurrentRevision = latest?.Revision,
                },
            };
        }

        var updatedGame = store.TryGetGame(gameId)!;
        _state.Events.Publish("game.updated", $"game:{gameId}", new { gameId, revision = newRevision, translation = TranslationDto(updatedGame) }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TranslationDto(updatedGame),
        };
    }

    private static object TranslationDto(GameCard game)
    {
        var inherited = game.TranslationInherited
            ? TranslationRequirement.Required
            : TranslationRequirement.Auto;
        var userOverride = game.TranslationOverride is null
            ? TranslationRequirement.Auto
            : Enum.Parse<TranslationRequirement>(game.TranslationOverride, ignoreCase: false);
        var policy = TranslationPolicy.FromInheritance(
            new FolderClassification([], inherited == TranslationRequirement.Required, game.TranslationInherited ? "[toolNeed]" : null, ClassificationRules.CurrentVersion))
            with
        { UserOverride = userOverride };
        return new
        {
            gameId = game.GameId,
            userOverride = userOverride.ToString(),
            inherited = inherited.ToString(),
            inheritedFrom = game.TranslationInherited ? "[toolNeed]" : null,
            effective = policy.Effective.ToString(),
            isRequired = policy.IsRequired,
            revision = game.Revision,
        };
    }

    private Envelope<object> ProfilesSetDefault(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "profiles set_default 需要 gameId、profileId 参数");
        }

        try
        {
            var updated = _state.Launches.SetDefault(gameId, profileId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = ProfileDto(updated),
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    private Envelope<object> ProfilesRemove(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "缺少 profileId 参数");
        }

        try
        {
            var removed = _state.Launches.RemoveProfile(profileId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new { profileId = removed.ProfileId, gameId = removed.GameId, removed = true },
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex) when (ex.Code == ErrorCodes.InvalidArgument)
        {
            // 默认配置移除需明确替代项（契约 3.1）：给出可执行修复入口。
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
                    RecoveryOperation = "profiles.set_default",
                },
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    private Envelope<object> ProfilesValidate(IpcRequest request)
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

        var issues = new List<object>();
        if (!File.Exists(profile.ExecutablePath))
        {
            issues.Add(new { code = "EntryMissing", detail = $"入口不存在：{profile.ExecutablePath}" });
        }

        if (!Directory.Exists(profile.WorkingDirectory))
        {
            issues.Add(new { code = "WorkingDirectoryMissing", detail = $"工作目录不存在：{profile.WorkingDirectory}" });
        }

        if (profile.ToolId is not null)
        {
            issues.Add(new
            {
                code = "ToolUnverified",
                detail = $"绑定工具 {profile.ToolId} 的能力验证状态用 verification.list 查询；validate 不执行工具",
            });
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                profileId = profile.ProfileId,
                gameId = profile.GameId,
                available = issues.Count == 0,
                toolId = profile.ToolId,
                isDefault = profile.IsDefault,
                issues,
            },
        };
    }

    /// <summary>诊断状态（T24）：进程/库/审计日志统计与队列指标；不含任何业务数据原文。</summary>
    private Envelope<object> DiagnosticsStatus(IpcRequest request)
    {
        var (currentFile, currentBytes, fileCount) = _state.AuditLog.Describe();
        var library = _state.Library;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                processId = _state.Identity.ProcessId,
                startedAtUtc = _state.Identity.StartedAtUtc.ToString("O"),
                appVersion = _state.Identity.AppVersion,
                apiVersion = ApiConstants.ApiVersion,
                library = new
                {
                    status = library.Status.ToString(),
                    initialized = library.Initialized,
                    schemaVersion = library.SchemaVersion,
                    detail = LogSanitizer.Sanitize(library.Detail, _state.DataDirectory),
                },
                audit = new
                {
                    currentFile,
                    currentBytes,
                    fileCount,
                    maxFileBytes = Observability.AuditLogWriter.DefaultMaxFileBytes,
                    maxFiles = Observability.AuditLogWriter.DefaultMaxFiles,
                    retentionDays = Observability.AuditLogWriter.DefaultRetentionDays,
                },
                jobs = new { activeCount = _state.Jobs.ActiveJobCount() },
            },
        };
    }

    /// <summary>脱敏审计日志读取（diagnostics.logs）：返回最近 limit 条审计记录。</summary>
    private Envelope<object> DiagnosticsLogs(IpcRequest request)
    {
        var limit = 100;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } logParameters
            && logParameters.TryGetProperty("limit", out var limitElement)
            && limitElement.ValueKind == JsonValueKind.Number
            && limitElement.TryGetInt32(out var parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 1000);
        }

        var lines = _state.AuditLog.ReadRecentLines(limit);
        var records = new List<object>();
        foreach (var line in lines)
        {
            try
            {
                records.Add(JsonSerializer.Deserialize<JsonElement>(line, ContractJson.Options).Clone());
            }
            catch (JsonException)
            {
                // 单行损坏不阻塞诊断读取。
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = records.Count, items = records },
        };
    }

    /// <summary>
    /// 工具发现（T07/T09，tools.discover）：按 tool 分派——mtool（默认，游戏根配方）、
    /// renpythief（安装指纹与 Guided 计划）、player（常见播放器发现+本地文件参数模板）、
    /// steam（注册表/常见路径发现 + appmanifest 清单）。只读，不自启动任何进程；
    /// 调用方路径仍经库根白名单收口。
    /// </summary>
    private Envelope<object> ToolsDiscover(IpcRequest request)
    {
        TryGetStringParameter(request, "tool", out var discoverTool);
        var hasPath = TryGetStringParameter(request, "path", out var path) && path.Length > 0;

        if (string.Equals(discoverTool, "steam", StringComparison.Ordinal))
        {
            var steam = new Infrastructure.Tools.SteamAdapter().Discover();
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    toolId = "steam",
                    found = steam.Found,
                    steamRoot = steam.SteamRoot,
                    steamExecutablePath = steam.SteamExecutablePath,
                    manifestMissing = steam.ManifestMissing,
                    capability = steam.Capability,
                    manifests = steam.Manifests.Select(m => new
                    {
                        appId = m.AppId,
                        name = m.Name,
                        installDir = m.InstallDir,
                    }).ToArray(),
                    notice = LogSanitizer.Sanitize(steam.Notice, _state.DataDirectory),
                },
            };
        }

        if (string.Equals(discoverTool, "player", StringComparison.Ordinal))
        {
            var players = new Infrastructure.Tools.PlayerAdapter().Discover();
            string? templateJson = null;
            string? templateTarget = null;
            if (hasPath && File.Exists(path) && players.Count > 0)
            {
                var validation = Domain.Paths.GamePath.TryCreate(path);
                if (validation.IsValid && _state.Roots.Contains(validation.Path!.PhysicalPath))
                {
                    var template = players[0] is { } first
                        ? new Infrastructure.Tools.PlayerAdapter().BuildLaunchTemplate(first, validation.Path.PhysicalPath)
                        : null;
                    if (template is not null)
                    {
                        templateTarget = validation.Path.PhysicalPath;
                        templateJson = JsonSerializer.Serialize(new
                        {
                            player = players[0].Name,
                            executablePath = template.ExecutablePath,
                            argv = template.Arguments,
                            cwd = template.WorkingDirectory,
                            waitForExit = template.WaitForExit,
                        }, ContractJson.Options);
                    }
                }
            }

            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    toolId = "player",
                    players = players.Select(p => new { name = p.Name, executablePath = p.ExecutablePath }).ToArray(),
                    templateFor = templateTarget,
                    template = templateJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(templateJson, ContractJson.Options).Clone(),
                    notice = "参数模板仅覆盖公开稳定行为（打开本地文件）；参数差异须逐播放器以用户样本验证后保存 ExternalPlayer 配置",
                },
            };
        }

        if (!hasPath)
        {
            return InvalidArgument(request, "缺少 path 参数（游戏根的绝对路径）");
        }

        var pathValidation = Domain.Paths.GamePath.TryCreate(path);
        if (!pathValidation.IsValid)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = pathValidation.IsUnsupported ? ErrorCodes.UnsupportedPath : ErrorCodes.InvalidPath,
                    Message = $"路径非法（{pathValidation.Reason}）：{path}",
                    Retryable = false,
                },
            };
        }

        if (RejectPathOutsideRoots(request, pathValidation.Path!.PhysicalPath) is { } discoverOutsideRoot)
        {
            return discoverOutsideRoot;
        }

        if (!Directory.Exists(pathValidation.Path.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"路径不存在或离线：{pathValidation.Path.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        if (string.Equals(discoverTool, "renpythief", StringComparison.Ordinal))
        {
            var renpy = new Infrastructure.Tools.RenpyThiefAdapter().Discover(pathValidation.Path.PhysicalPath);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    toolId = "renpythief",
                    path = pathValidation.Path.PhysicalPath,
                    found = renpy.Found,
                    evidenceKind = "Static",
                    capability = renpy.Capability,
                    fingerprint = renpy.Fingerprint,
                    mainExecutablePath = renpy.MainExecutablePath,
                    launcherPath = renpy.LauncherPath,
                    guidedPlan = renpy.GuidedPlan is null ? null : new
                    {
                        executablePath = renpy.GuidedPlan.ExecutablePath,
                        argv = renpy.GuidedPlan.Arguments,
                        cwd = renpy.GuidedPlan.WorkingDirectory,
                    },
                    notice = LogSanitizer.Sanitize(renpy.Notice, _state.DataDirectory),
                },
            };
        }

        var mtoolDiscovery = new Infrastructure.Tools.MToolAdapter().Discover(pathValidation.Path.PhysicalPath);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                toolId = Infrastructure.Tools.MToolDiscovery.ToolId,
                path = pathValidation.Path.PhysicalPath,
                evidenceKind = mtoolDiscovery.EvidenceKind,
                capability = mtoolDiscovery.Capability,
                recipe = mtoolDiscovery.Recipe is null
                    ? null
                    : new
                    {
                        sourcePath = mtoolDiscovery.Recipe.SourcePath,
                        scriptSha256 = mtoolDiscovery.Recipe.ScriptSha256,
                        isBroken = mtoolDiscovery.Recipe.IsBroken,
                        brokenPaths = mtoolDiscovery.Recipe.BrokenPaths,
                        referencedFiles = mtoolDiscovery.Recipe.ReferencedFiles,
                        steps = mtoolDiscovery.Recipe.Steps.Select(step => new
                        {
                            sequence = step.Sequence,
                            executablePath = step.ExecutablePath,
                            argv = step.Arguments,
                            cwd = step.WorkingDirectory,
                            waitForExit = step.WaitForExit,
                        }).ToArray(),
                    },
                unsupportedReason = mtoolDiscovery.UnsupportedReason,
                notice = LogSanitizer.Sanitize(mtoolDiscovery.Notice, _state.DataDirectory),
            },
        };
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private const long MaxAssetBytes = 1 * 1024 * 1024;

    /// <summary>资料字段设置（T14，fields.set）：Revision 即游戏卡片 Revision；title 变更镜像到 games 列表。</summary>
    private Envelope<object> FieldsSet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "field", out var field)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "fields.set 需要 gameId、field、expectedRevision 参数");
        }

        if (field is not ("title" or "summary"))
        {
            return InvalidArgument(request, $"不支持的字段：{field}（当前支持 title、summary）");
        }

        string? value = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } fsParameters
            && fsParameters.TryGetProperty("value", out var valueElement)
            && valueElement.ValueKind == JsonValueKind.String)
        {
            value = valueElement.GetString();
        }

        var newRevision = store.SetGameField(gameId, field, value, "user", expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            var card = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = card is null ? ErrorCodes.NotFound : ErrorCodes.RevisionConflict,
                    Message = card is null ? $"游戏不存在：{gameId}" : $"Revision 不一致：期望 {expectedRevision}，当前 {card.Revision}",
                    Retryable = false,
                },
            };
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, field, value, source = "user", revision = newRevision },
        };
    }

    /// <summary>封面导入（T14，assets.import）：用户图片复制入应用自有目录，不反写游戏目录。</summary>
    private Envelope<object> AssetsImport(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "sourcePath", out var sourcePath))
        {
            return InvalidArgument(request, "assets.import 需要 gameId、sourcePath 参数");
        }

        if (store.TryGetGame(gameId) is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension))
        {
            return InvalidArgument(request, $"不支持的图片格式：{extension}（支持 {string.Join("/", ImageExtensions)}）");
        }

        if (!File.Exists(sourcePath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.NotFound,
                    Message = $"源图片不存在：{sourcePath}",
                    Retryable = false,
                },
            };
        }

        if (new FileInfo(sourcePath).Length > MaxAssetBytes)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ResourceTooLarge,
                    Message = $"图片超过 1 MiB 上限：{sourcePath}",
                    Retryable = false,
                },
            };
        }

        var assetDirectory = Path.Combine(_state.DataDirectory, "assets", gameId);
        Directory.CreateDirectory(assetDirectory);
        var importedPath = Path.Combine(assetDirectory, $"{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, importedPath, overwrite: false);

        var asset = store.ImportAsset(gameId, importedPath, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = AssetDto(asset),
        };
    }

    private Envelope<object> AssetsList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var listGameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var assets = store.ListAssets(listGameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = assets.Count, items = assets.Select(AssetDto).ToArray() },
        };
    }

    /// <summary>资产读取（契约 5.x）：受限预览 ≤1 MiB，base64 返回。</summary>
    private Envelope<object> AssetsGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "assetId", out var assetId))
        {
            return InvalidArgument(request, "缺少 assetId 参数");
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null)
        {
            return NotFound(request, $"资产不存在：{assetId}");
        }

        if (!File.Exists(asset.FilePath))
        {
            return NotFound(request, $"资产文件缺失：{asset.FilePath}");
        }

        var bytes = File.ReadAllBytes(asset.FilePath);
        if (bytes.Length > MaxAssetBytes)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ResourceTooLarge,
                    Message = "资产超过 1 MiB 预览上限",
                    Retryable = false,
                },
            };
        }

        var mimeType = asset.FilePath switch
        {
            var p when p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => "image/png",
            var p when p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) => "image/gif",
            var p when p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) => "image/webp",
            _ => "image/jpeg",
        };

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                assetId = asset.AssetId,
                gameId = asset.GameId,
                kind = asset.Kind,
                isCurrent = asset.IsCurrent,
                mimeType,
                sizeBytes = bytes.LongLength,
                dataBase64 = Convert.ToBase64String(bytes),
            },
        };
    }

    /// <summary>
    /// 用户主动清空（fields.clear）：字段层 value=null（≠继承自动值）；title 镜像为空串。
    /// </summary>
    private Envelope<object> FieldsClear(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "field", out var field)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null
            || field is not ("title" or "summary"))
        {
            return InvalidArgument(request, "fields.clear 需要 gameId、field（title/summary）、expectedRevision 参数");
        }

        var newRevision = store.SetGameField(gameId, field, null, "user", expectedRevision.Value, DateTime.UtcNow);
        return FieldRevisionResult(request, gameId, field, newRevision, "user");
    }

    /// <summary>恢复自动值（fields.reset）：删除用户层；title 回退自动层值（首次覆盖前自动层已登记）。</summary>
    private Envelope<object> FieldsReset(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "field", out var field)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null
            || field is not ("title" or "summary"))
        {
            return InvalidArgument(request, "fields.reset 需要 gameId、field（title/summary）、expectedRevision 参数");
        }

        var card = store.TryGetGame(gameId);
        if (card is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var fallback = field == "title"
            ? Path.GetFileName(card.RootPath.TrimEnd(Path.DirectorySeparatorChar)) ?? ""
            : "";
        var newRevision = store.ResetGameField(gameId, field, fallback, expectedRevision.Value, DateTime.UtcNow);
        return FieldRevisionResult(request, gameId, field, newRevision, "auto");
    }

    private static Envelope<object> FieldRevisionResult(IpcRequest request, string gameId, string field, int? newRevision, string source)
    {
        if (newRevision is null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = "Revision 不一致（游戏卡片可能已被其他入口修改）",
                    Retryable = false,
                },
            };
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, field, source, revision = newRevision },
        };
    }

    /// <summary>选择候选封面（assets.choose）：校验游戏 Revision；封面切换不递增卡片 Revision。</summary>
    private Envelope<object> AssetsChoose(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "assetId", out var assetId)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "assets.choose 需要 gameId、assetId、expectedRevision 参数");
        }

        var card = store.TryGetGame(gameId);
        if (card is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        if (card.Revision != expectedRevision.Value)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"Revision 不一致：期望 {expectedRevision}，当前 {card.Revision}",
                    Retryable = false,
                },
            };
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null || !string.Equals(asset.GameId, gameId, StringComparison.Ordinal))
        {
            return NotFound(request, $"资产不存在或不属于该游戏：{assetId}");
        }

        store.ChooseAsset(gameId, assetId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, assetId, isCurrent = true },
        };
    }

    /// <summary>裁切封面（assets.crop）：真实像素裁切，产出新资产并设为当前封面。</summary>
    private Envelope<object> AssetsCrop(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "assetId", out var assetId)
            || !TryGetIntParameter(request, "x", out var x) || x is null
            || !TryGetIntParameter(request, "y", out var y) || y is null
            || !TryGetIntParameter(request, "width", out var width) || width is null
            || !TryGetIntParameter(request, "height", out var height) || height is null)
        {
            return InvalidArgument(request, "assets.crop 需要 assetId、x、y、width、height 参数");
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null)
        {
            return NotFound(request, $"资产不存在：{assetId}");
        }

        if (!File.Exists(asset.FilePath))
        {
            return NotFound(request, $"资产文件缺失：{asset.FilePath}");
        }

        try
        {
            var destDirectory = Path.Combine(_state.DataDirectory, "assets", asset.GameId);
            var croppedPath = ImageCropper.Crop(
                asset.FilePath, destDirectory, x.Value, y.Value, width.Value, height.Value);
            var newAsset = store.ImportAsset(asset.GameId, croppedPath, DateTime.UtcNow);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    sourceAssetId = asset.AssetId,
                    newAssetId = newAsset.AssetId,
                    isCurrent = true,
                    x = x.Value,
                    y = y.Value,
                    width = width.Value,
                    height = height.Value,
                },
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException
            or InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            return InvalidArgument(request, $"裁切失败：{ex.Message}");
        }
    }

    /// <summary>重置封面（assets.reset）：全部封面置为非当前，游戏回到无封面展示。</summary>
    private Envelope<object> AssetsReset(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var previous = store.ResetCover(gameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, previousAssetId = previous, isCurrent = false },
        };
    }

    /// <summary>移除资产（assets.remove）：仅限应用自有且非当前引用的资源。</summary>
    private Envelope<object> AssetsRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "assetId", out var assetId))
        {
            return InvalidArgument(request, "缺少 assetId 参数");
        }

        var removedPath = store.RemoveAsset(assetId);
        if (removedPath is null)
        {
            var asset = store.TryGetAsset(assetId);
            return asset is null
                ? NotFound(request, $"资产不存在：{assetId}")
                : InvalidArgument(request, "当前封面不可移除；先 choose 其他封面或 reset");
        }

        try
        {
            File.Delete(removedPath);
        }
        catch (IOException)
        {
            // 行已删；文件残留不阻塞（仅应用自有副本）。
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { assetId, removed = true },
        };
    }

    /// <summary>
    /// 元数据建议预览（metadata.preview）：仅本地证据——自动标题（根目录名）与
    /// 引擎/入口描述；无在线元数据源，如实标注。
    /// </summary>
    private Envelope<object> MetadataPreview(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var autoTitle = Path.GetFileName(game.RootPath.TrimEnd(Path.DirectorySeparatorChar)) ?? game.Title;
        var autoSummary = $"自动识别：引擎 {game.Engine ?? "未识别"}，入口 {game.EntryPath ?? "未确定"}。";

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                gameId,
                note = "仅本地证据建议；无在线元数据源，建议一律 source=auto，refresh 只更新 AutoValue 不覆盖用户层",
                suggestions = new object[]
                {
                    new { field = "title", value = autoTitle, source = "auto", evidence = "安装根目录名（目录名仅 contextual）" },
                    new { field = "summary", value = autoSummary, source = "auto", evidence = "本地检测证据（引擎/入口）" },
                },
            },
        };
    }

    /// <summary>元数据刷新（metadata.refresh）：作业式更新 AutoValue，不覆盖用户层。</summary>
    private Envelope<object> MetadataRefresh(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var autoTitle = Path.GetFileName(game.RootPath.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        var autoSummary = $"自动识别：引擎 {game.Engine ?? "未识别"}，入口 {game.EntryPath ?? "未确定"}。";

        var jobId = _state.Jobs.Create("metadata-refresh", context =>
        {
            store.WriteAutoField(gameId, "title", autoTitle, DateTime.UtcNow);
            store.WriteAutoField(gameId, "summary", autoSummary, DateTime.UtcNow);
            context.ReportProgress(new { gameId, updatedFields = new[] { "title", "summary" } });
            return Task.FromResult(JobOutcome.Succeeded());
        });

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, gameId, state = "running" },
        };
    }

    /// <summary>事件增量读取（T16，events.read）：游标不跨重启；过期返回 CursorExpired。</summary>
    private Envelope<object> EventsRead(IpcRequest request)
    {
        long? cursor = null;
        int limit = 100;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } erParameters)
        {
            if (erParameters.TryGetProperty("cursor", out var cursorElement)
                && cursorElement.ValueKind == JsonValueKind.Number
                && cursorElement.TryGetInt64(out var parsedCursor))
            {
                cursor = parsedCursor;
            }

            if (erParameters.TryGetProperty("limit", out var limitElement)
                && limitElement.ValueKind == JsonValueKind.Number
                && limitElement.TryGetInt32(out var parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 4096);
            }
        }

        var events = _state.Events.ReadAfter(cursor, limit);
        if (events is null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.CursorExpired,
                    Message = "事件游标已过期（宿主重启或事件已被淘汰）；请不带 cursor 重新全量读取",
                    Retryable = false,
                },
            };
        }

        var nextCursor = events.Count > 0 ? events[events.Count - 1].Sequence : cursor ?? 0;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                nextCursor,
                items = events.Select(ev => new
                {
                    sequence = ev.Sequence,
                    timestampUtc = ev.TimestampUtc.ToString("O"),
                    type = ev.Type,
                    entityKey = ev.EntityKey,
                    payload = JsonSerializer.Deserialize<JsonElement>(ev.PayloadJson, ContractJson.Options).Clone(),
                }).ToArray(),
            },
        };
    }

    /// <summary>开始一次工具验证（T08）：绑定当前指纹与隔离样本，状态 Unknown。</summary>
    private Envelope<object> VerificationStart(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "toolId", out var toolId)
            || !TryGetStringParameter(request, "fingerprint", out var fingerprint)
            || !TryGetStringParameter(request, "engine", out var engine)
            || !TryGetStringParameter(request, "samplePath", out var samplePath))
        {
            return InvalidArgument(request, "verification.start 需要 toolId、fingerprint、engine、samplePath 参数");
        }

        var record = new GameLibrary.Domain.Tools.ToolVerificationRecord
        {
            RecordId = $"verif-{Guid.NewGuid():N}",
            ToolId = toolId,
            ToolFingerprint = fingerprint,
            Engine = engine,
            SamplePath = samplePath,
            Status = GameLibrary.Domain.Tools.ToolVerificationStatus.Unknown,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.InsertVerification(record);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    /// <summary>提交验证观察（双结论分开累积；翻译生效必须先有游戏启动证据）。</summary>
    private Envelope<object> VerificationReport(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "recordId", out var recordId))
        {
            return InvalidArgument(request, "缺少 recordId 参数");
        }

        var record = store.TryGetVerification(recordId);
        if (record is null)
        {
            return NotFound(request, $"验证记录不存在：{recordId}");
        }

        TryGetBoolParameter(request, "gameStarted", out var gameStarted);
        TryGetBoolParameter(request, "translationConfirmed", out var translationConfirmed);

        // 指纹校验：工具更新/换目录后旧记录失效，需重新验证。
        if (TryGetStringParameter(request, "fingerprint", out var fingerprint)
            && !string.Equals(fingerprint, record.ToolFingerprint, StringComparison.Ordinal))
        {
            record = record with
            {
                Status = GameLibrary.Domain.Tools.ToolVerificationStatus.Unknown,
                Note = "工具指纹变化，历史验证失效",
                UpdatedUtc = DateTime.UtcNow,
            };
            store.UpdateVerification(record);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ToolChanged,
                    Message = "工具指纹与验证记录不一致；记录已失效，请重新验证",
                    Retryable = false,
                },
            };
        }

        var newStatus = GameLibrary.Domain.Tools.ToolVerificationRules.ApplyObservation(
            record.Status, gameStartedConfirmed: gameStarted == true, translationConfirmed: translationConfirmed == true);
        record = record with
        {
            Status = newStatus,
            GameStartedConfirmed = record.GameStartedConfirmed || gameStarted == true,
            TranslationConfirmed = record.TranslationConfirmed || translationConfirmed == true,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.UpdateVerification(record);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    /// <summary>使验证记录失效（工具更新/用户撤销）。</summary>
    private Envelope<object> VerificationInvalidate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "recordId", out var recordId))
        {
            return InvalidArgument(request, "缺少 recordId 参数");
        }

        var record = store.TryGetVerification(recordId);
        if (record is null)
        {
            return NotFound(request, $"验证记录不存在：{recordId}");
        }

        record = record with
        {
            Status = GameLibrary.Domain.Tools.ToolVerificationStatus.Unknown,
            Note = "验证已失效（invalidate）",
            UpdatedUtc = DateTime.UtcNow,
        };
        store.UpdateVerification(record);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    private Envelope<object> VerificationGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "recordId", out var recordId))
        {
            return InvalidArgument(request, "缺少 recordId 参数");
        }

        var record = store.TryGetVerification(recordId);
        if (record is null)
        {
            return NotFound(request, $"验证记录不存在：{recordId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    private Envelope<object> VerificationList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        string? toolId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } vlParameters
            && vlParameters.TryGetProperty("toolId", out var toolElement)
            && toolElement.ValueKind == JsonValueKind.String)
        {
            toolId = toolElement.GetString();
        }

        var records = store.ListVerifications(toolId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = records.Count, items = records.Select(VerificationDto).ToArray() },
        };
    }

    private static object VerificationDto(GameLibrary.Domain.Tools.ToolVerificationRecord record) => new
    {
        recordId = record.RecordId,
        toolId = record.ToolId,
        toolFingerprint = record.ToolFingerprint,
        engine = record.Engine,
        samplePath = record.SamplePath,
        status = record.Status,
        gameStartedConfirmed = record.GameStartedConfirmed,
        translationConfirmed = record.TranslationConfirmed,
        note = record.Note,
        createdUtc = record.CreatedUtc.ToString("O"),
        updatedUtc = record.UpdatedUtc.ToString("O"),
    };

    private static object AssetDto(GameAsset asset) => new
    {
        assetId = asset.AssetId,
        gameId = asset.GameId,
        kind = asset.Kind,
        isCurrent = asset.IsCurrent,
        importedUtc = asset.ImportedUtc.ToString("O"),
    };

    private object GameDto(SqliteLibraryStore store, GameCard game)
    {
        var (title, titleSource) = store.EffectiveField(game.GameId, "title", game.Title);
        var (summary, summarySource) = store.EffectiveField(game.GameId, "summary", "");
        var coverAssetId = store.ListAssets(game.GameId).FirstOrDefault(a => a.IsCurrent)?.AssetId;
        return new
        {
            gameId = game.GameId,
            favorite = game.Favorite,
            title = title ?? "",
            titleSource,
            summary,
            summarySource,
            coverAssetId,
            rootPath = game.RootPath,
            kind = game.Kind,
            engine = game.Engine,
            entryPath = game.EntryPath,
            membership = game.Membership,
            revision = game.Revision,
            acceptedUtc = game.AcceptedUtc.ToString("O"),
        };
    }

    private Envelope<object> IgnoresList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var rules = store.ListIgnoreRules();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = rules.Count, items = rules.Select(IgnoreDto).ToArray() },
        };
    }

    private Envelope<object> IgnoresCreate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "scope", out var scope)
            || scope is not ("ExactPath" or "Subtree" or "ConfirmedIdentity"))
        {
            return InvalidArgument(request, "缺少 scope 参数（ExactPath/Subtree/ConfirmedIdentity）");
        }

        TryGetStringParameter(request, "path", out var path);
        TryGetStringParameter(request, "gameId", out var gameId);
        TryGetStringParameter(request, "reason", out var reason);
        if (scope != "ConfirmedIdentity" && path.Length == 0)
        {
            return InvalidArgument(request, $"{scope} 需要 path 参数（规范化绝对路径）");
        }

        if (scope == "ConfirmedIdentity" && gameId.Length == 0)
        {
            return InvalidArgument(request, "ConfirmedIdentity 需要用户确认的 gameId");
        }

        if (path.Length > 0 && !_state.Roots.Contains(path))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.PermissionDenied,
                    Message = $"路径不在已注册库根内：{path}",
                    Retryable = false,
                },
            };
        }

        var rule = new IgnoreRule
        {
            IgnoreId = $"ignore-{Guid.NewGuid():N}",
            Scope = scope,
            Path = path.Length > 0 ? path : null,
            GameId = gameId.Length > 0 ? gameId : null,
            Reason = reason.Length > 0 ? reason : null,
            CreatedUtc = DateTime.UtcNow,
        };
        store.InsertIgnoreRule(rule);

        // 抑制立即生效：撤销前匹配的待审核候选转入 ignored（幂等补登记，不覆盖已有终态）。
        var suppressed = 0;
        foreach (var candidate in store.ListCandidates())
        {
            if (candidate.ReviewState is "observed" or "stabilizing" or "pendingReview"
                && store.IsSuppressedByIgnoreRule(candidate.PhysicalPath, candidate.GameId))
            {
                var transitioned = store.TransitionCandidate(
                    candidate.CandidateId, candidate.ReviewState, "ignored", candidate.Revision, null, DateTime.UtcNow);
                if (transitioned is not null)
                {
                    suppressed++;
                }
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { ignoreId = rule.IgnoreId, scope, suppressedCandidates = suppressed },
        };
    }

    /// <summary>撤销忽略（恢复候选提示的唯一途径）：匹配的 ignored 候选回到 Observed（状态机 Ignored→Observed）。</summary>
    private Envelope<object> IgnoresRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "ignoreId", out var ignoreId))
        {
            return InvalidArgument(request, "缺少 ignoreId 参数");
        }

        var rule = store.ListIgnoreRules()
            .FirstOrDefault(r => string.Equals(r.IgnoreId, ignoreId, StringComparison.Ordinal));
        if (rule is null)
        {
            return NotFound(request, $"忽略规则不存在：{ignoreId}");
        }

        TryGetIntParameter(request, "expectedRevision", out var expectedRevision);
        if (expectedRevision is not null && expectedRevision.Value != rule.Revision)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"忽略规则 Revision 不一致：期望 {expectedRevision}，当前 {rule.Revision}",
                    Retryable = false,
                },
            };
        }

        var coveredPaths = store.RemoveIgnoreRule(ignoreId);
        var restored = 0;
        foreach (var candidate in store.ListCandidates())
        {
            if (candidate.ReviewState != "ignored")
            {
                continue;
            }

            var candidatePath = candidate.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
            var covered = coveredPaths.Any(p =>
                string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), candidatePath, StringComparison.OrdinalIgnoreCase));
            if (!covered && rule.Scope == "Subtree" && rule.Path is not null)
            {
                var rulePath = rule.Path.TrimEnd(Path.DirectorySeparatorChar);
                covered = candidatePath.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            if (covered
                && !store.IsSuppressedByIgnoreRule(candidate.PhysicalPath, candidate.GameId))
            {
                var transitioned = store.TransitionCandidate(
                    candidate.CandidateId, "ignored", "observed", candidate.Revision, null, DateTime.UtcNow);
                if (transitioned is not null)
                {
                    restored++;
                }
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { ignoreId, removed = true, restoredCandidates = restored },
        };
    }

    private static object IgnoreDto(IgnoreRule rule) => new
    {
        ignoreId = rule.IgnoreId,
        scope = rule.Scope,
        path = rule.Path,
        gameId = rule.GameId,
        reason = rule.Reason,
        revision = rule.Revision,
        createdUtc = rule.CreatedUtc.ToString("O"),
    };

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

        // T13：可选工具绑定与默认标记（isDefault 首个即默认，替代项走 profiles.set_default）。
        TryGetStringParameter(request, "toolId", out var toolId);
        TryGetBoolParameter(request, "isDefault", out var defaultFlag);
        var profile = _state.Launches.AddProfile(gameId, executablePath, argv, cwd, toolId.Length > 0 ? toolId : null, defaultFlag == true);
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
            var block = TranslationRouteBlock(request, gameId, plan.ProfileId);
            if (block is not null)
            {
                // 预览无副作用：计划照常返回，但明确 needsUserAction 与后续步骤，不让 agent 误以为可直接执行。
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.NeedsUserAction,
                    Data = plan.ToDto(),
                    NextActions =
                    [
                        new NextAction
                        {
                            OperationId = "tools.discover",
                            Reason = "游戏翻译策略为 Required；目标 Profile 未绑定翻译工具，直启会被拒绝",
                        },
                        new NextAction
                        {
                            OperationId = "translation.set",
                            Reason = "如需原文直启，请显式将策略覆盖为 NotRequired（用户主动选择，不静默回退）",
                        },
                    ],
                };
            }

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

    /// <summary>
    /// T13 Required 不回退（LA-07）：游戏翻译策略有效值为 Required 且目标 Profile 无工具绑定时，
    /// 返回阻断信封；null 表示翻译路由可直启（策略非 Required，或 Profile 已绑定工具）。
    /// </summary>
    private Envelope<object>? TranslationRouteBlock(IpcRequest request, string gameId, string resolvedProfileId)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return null;
        }

        var game = store.TryGetGame(gameId);
        var profile = _state.Launches.GetProfile(resolvedProfileId);
        if (game is null || profile is null)
        {
            return null;
        }

        var inherited = game.TranslationInherited
            ? TranslationRequirement.Required
            : TranslationRequirement.Auto;
        var userOverride = game.TranslationOverride is null
            ? TranslationRequirement.Auto
            : Enum.Parse<TranslationRequirement>(game.TranslationOverride, ignoreCase: false);
        if ((userOverride != TranslationRequirement.Auto ? userOverride : inherited) != TranslationRequirement.Required)
        {
            return null;
        }

        if (profile.ToolId is not null)
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
                Code = ErrorCodes.TranslationRouteUnavailable,
                Message = $"游戏翻译策略为 Required，而 Profile {resolvedProfileId} 是普通直启（无工具绑定）；不静默回退原文直启",
                Retryable = false,
            },
            NextActions =
            [
                new NextAction
                {
                    OperationId = "tools.discover",
                    Reason = "发现并绑定翻译工具（MTool/RenpyThief/播放器/steam）后创建翻译 Profile",
                },
                new NextAction
                {
                    OperationId = "translation.set",
                    Reason = "用户主动选择原文直启时，显式将策略覆盖为 NotRequired",
                },
            ],
        };
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

        // T13 Required 不回退：执行前解析目标 Profile（显式 profileId 或计划内的），
        // 游戏 Required 且该 Profile 无工具绑定 → 拒绝执行（LA-07），不产生尝试。
        var resolvedProfileId = profileId.Length > 0
            ? profileId
            : planId.Length > 0
                ? _state.Launches.GetPlanProfileId(planId)
                : null;
        if (resolvedProfileId is not null)
        {
            var resolvedGameId = profileId.Length > 0
                ? _state.Launches.GetProfile(resolvedProfileId)?.GameId
                : _state.Launches.GetPlanGameId(planId);
            if (resolvedGameId is not null)
            {
                var block = TranslationRouteBlock(request, resolvedGameId, resolvedProfileId);
                if (block is not null)
                {
                    return block;
                }
            }
        }

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
        toolId = profile.ToolId,
        isDefault = profile.IsDefault,
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

    private static bool TryGetBoolParameter(IpcRequest request, string name, out bool? value)
    {
        value = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (request.Parameters is { ValueKind: JsonValueKind.Object } falseParameters
            && falseParameters.TryGetProperty(name, out var falseElement)
            && falseElement.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

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
