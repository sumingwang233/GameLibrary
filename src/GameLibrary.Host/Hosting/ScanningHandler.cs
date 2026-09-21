using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 扫描域处理器：scan.start / scan.status / scan.cancel / scan.coverage / scan.inspect
/// 五操作 + jobs.get（经 JobSnapshotEnvelope，kind 无关可查任意作业含 backup）与
/// DefaultDetectors 随域整体迁入。Store 经委托每请求取当前值（library.init /
/// backups.restore 会整体替换 Library，禁止构造时缓存 store 引用）；Coordinator
/// 为 settable 属性（接线虽先于 dispatcher 构造，仍按可变成员经委托取值）；
/// Jobs/Candidates/Events/Roots 为 init-only 引用（HostRuntimeState 构造后整体
/// 不可替换）。由 DispatchCore 调用，天然继承幂等收据（scan.start 在
/// ReceiptOperations）与串行门、权限、维护模式等中间件。
/// </summary>
internal sealed class ScanningHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly JobManager _jobs;
    private readonly Func<ScanCoordinator> _coordinator;
    private readonly CandidateRegistry _candidates;
    private readonly EventStream _events;
    private readonly RootRegistry _roots;

    /// <summary>
    /// jobs/candidates/events/roots 以 init-only 引用直传：HostRuntimeState 构造后
    /// 整体不可替换（HostRuntime.cs）；Store 经委托每请求取当前值；Coordinator
    /// 为 settable 属性，经委托取值。
    /// </summary>
    public ScanningHandler(
        Func<SqliteLibraryStore?> storeAccessor,
        JobManager jobs,
        Func<ScanCoordinator> coordinator,
        CandidateRegistry candidates,
        EventStream events,
        RootRegistry roots)
    {
        _storeAccessor = storeAccessor;
        _jobs = jobs;
        _coordinator = coordinator;
        _candidates = candidates;
        _events = events;
        _roots = roots;
    }

    /// <summary>scan.start（手动扫描作业）：候选收集 → 落库 → 可用性核对 → 事件发布。</summary>
    public Envelope<object> ScanStart(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "root", out var root))
        {
            return IpcRequests.InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
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

        if (IpcRequests.RejectPathOutsideRoots(request, rootPath.PhysicalPath, _roots) is { } outsideRoot)
        {
            return outsideRoot;
        }

        var jobId = _jobs.Create(
            "scan",
            context =>
            {
                _coordinator().ManualScanRunning = true;
                try
                {
                    var collector = new ScanCandidateCollector(rootPath, context.JobId, _candidates);
                    // 规则在作业启动时快照：扫描期间的 ignores 变更自下一次扫描生效。
                    var rules = ScanIgnoreRuleSet.FromStore(_storeAccessor());
                    ScanCoverageData? completedCoverage = null;
                    var outcome = ScanJobRunner.Run(
                        rootPath,
                        context,
                        collector,
                        onCompleted: coverage => completedCoverage = coverage,
                        rules: rules);
                    if (outcome.FinalState == "succeeded")
                    {
                        ScanCandidatePersistence.Persist(
                            _storeAccessor(),
                            _events,
                            collector,
                            context.JobId,
                            readyForReview: true,
                            requireRegisteredRoot: true);
                        // T17：完整扫描成功后核对库内游戏可用性（ID-04/05）。
                        // store 取局部：委托调用不保留 null 流状态（原属性链同点求值等价）。
                        var libraryStore = _storeAccessor();
                        if (libraryStore is not null
                            && completedCoverage?.Completion == ScanCompletion.Complete)
                        {
                            var report = ReconcileService.CheckGames(libraryStore, DateTime.UtcNow);
                            foreach (var transition in report.Transitions)
                            {
                                _events.Publish("game.updated", $"game:{transition.GameId}", new
                                {
                                    gameId = transition.GameId,
                                    availability = transition.To,
                                }, DateTime.UtcNow);
                            }
                        }

                        _events.Publish("scan.completed", $"job:{context.JobId}", new
                        {
                            jobId = context.JobId,
                            kind = "manual",
                            root = rootPath.PhysicalPath,
                            completion = completedCoverage?.Completion.ToString().ToLowerInvariant(),
                        }, DateTime.UtcNow);
                    }

                    return Task.FromResult(outcome);
                }
                finally
                {
                    _coordinator().ManualScanRunning = false;
                }
            },
            ScanJobRunner.InitialProgress(rootPath));

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, kind = "scan", state = "running" },
        };
    }

    /// <summary>作业快照信封：scan.status 与 jobs.get 共用；expectedKind 为 null 时
    /// kind 无关（jobs.get 可查任意作业含 backup），scan.status 限定 kind=="scan"。</summary>
    public Envelope<object> JobSnapshotEnvelope(IpcRequest request, string? expectedKind)
    {
        if (!IpcRequests.TryGetStringParameter(request, "jobId", out var jobId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 jobId 参数");
        }

        var snapshot = _jobs.Get(jobId);
        if (snapshot is null)
        {
            return IpcRequests.NotFound(request, $"作业不存在：{jobId}");
        }

        if (expectedKind is not null && !string.Equals(snapshot.Kind, expectedKind, StringComparison.Ordinal))
        {
            return IpcRequests.InvalidArgument(request, $"作业 {jobId} 类型是 {snapshot.Kind}，不是 {expectedKind}");
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

    /// <summary>scan.cancel：请求取消（返回 cancelRequested，不谎称已取消）。</summary>
    public Envelope<object> ScanCancel(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "jobId", out var jobId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 jobId 参数");
        }

        if (!_jobs.RequestCancel(jobId))
        {
            return _jobs.Get(jobId) is null
                ? IpcRequests.NotFound(request, $"作业不存在：{jobId}")
                : IpcRequests.InvalidArgument(request, $"作业已进入终态，无法取消：{jobId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { jobId, state = "cancelRequested" },
        };
    }

    /// <summary>scan.coverage：作业进度与覆盖数据快照。</summary>
    public Envelope<object> ScanCoverage(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "jobId", out var jobId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 jobId 参数");
        }

        var progress = _jobs.TryGetProgress(jobId);
        if (progress is null)
        {
            return IpcRequests.NotFound(request, $"作业不存在：{jobId}");
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
    public Envelope<object> ScanInspect(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "path", out var path))
        {
            return IpcRequests.InvalidArgument(request, "缺少 path 参数（绝对本地目录路径）");
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

        if (IpcRequests.RejectPathOutsideRoots(request, validation.Path.PhysicalPath, _roots) is { } inspectOutsideRoot)
        {
            return inspectOutsideRoot;
        }

        var snapshot = new FileSystemDirectorySnapshot(validation.Path);
        var report = new EngineDetectorSet(DefaultDetectors()).DetectAll(snapshot);
        var confirmed = report.Results
            .Where(r => r.Confidence >= DetectionConfidence.Medium)
            .ToArray();
        var generic = confirmed.Length == 0
            ? GenericGameCandidateDetector.Inspect(snapshot)
            : new GenericCandidateFinding([], []);
        var entryCandidates = confirmed.Length > 0
            ? confirmed.SelectMany(result => result.EntryCandidates).ToArray()
            : generic.EntryCandidates;
        var evidence = confirmed.Length > 0
            ? report.Results.SelectMany(result => result.Evidence).ToArray()
            : generic.Evidence;

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                path = validation.Path.PhysicalPath,
                recognized = confirmed.Length > 0 || generic.IsCandidate,
                candidateKind = confirmed.Length > 0 ? "gameRoot" : generic.IsCandidate ? "unknown" : null,
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
                entryCandidates = entryCandidates.Select(e => new
                {
                    relativePath = e.RelativePath,
                    score = e.Score,
                    reasons = e.Reasons,
                }).ToArray(),
                evidence = evidence.Select(e => new
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

    private static IEngineDetector[] DefaultDetectors() =>
    [
        new UnityDetector(),
        new RpgMakerMvMzDetector(),
        new RenpyDetector(),
        new KirikiriDetector(),
        new FlashDetector(),
    ];
}
