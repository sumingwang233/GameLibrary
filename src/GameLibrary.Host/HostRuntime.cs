using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Paths;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Ipc;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host;

/// <summary>
/// 宿主运行时装配：数据目录守卫 + 库状态 + 管道服务。Host 是唯一库连接所有者；
/// 库未初始化/损坏时宿主仍启动并如实上报状态（仅诊断能力可用，扫描/业务随后置判断）。
/// v1 审查修复：库根/启动 Profile/启动历史/作业记录从持久层恢复；
/// 运行态写入经宿主单写队列（Store 内部锁）串行化。
/// </summary>
public sealed class HostRuntime : IAsyncDisposable
{
    private readonly SingleInstanceGuard _guard;
    private readonly PipeServer _server;
    private readonly HostRuntimeState _state;

    private HostRuntime(SingleInstanceGuard guard, PipeServer server, HostRuntimeState state)
    {
        _guard = guard;
        _server = server;
        _state = state;
    }

    public HostIdentity Identity => _state.Identity;

    public HostLibraryState Library => _state.Library;

    public string DataDirectory => _state.DataDirectory;

    public static async Task<HostRuntime> StartAsync(
        string dataDirectory,
        ILoggerFactory loggerFactory,
        Action? notifyStopRequested = null,
        CancellationToken ct = default)
    {
        var resolved = Contracts.Ipc.DataDirectory.Resolve(dataDirectory);
        if (!resolved.IsValid)
        {
            throw new InvalidOperationException($"数据目录非法：{resolved.Error}");
        }

        var guard = SingleInstanceGuard.TryAcquire(resolved.CanonicalPath!, resolved.ComparisonKey!);
        if (!guard.IsPrimary)
        {
            guard.Dispose();
            throw new InvalidOperationException("该数据目录已有宿主运行");
        }

        var identity = new HostIdentity();
        var library = await OpenLibraryAsync(resolved.CanonicalPath!, identity, loggerFactory, ct);
        var events = new EventStream(library.Store);
        var metrics = new Observability.HostMetrics();
        var runtimeState = new HostRuntimeState
        {
            Identity = identity,
            DataDirectory = resolved.CanonicalPath!,
            Library = library,
            Jobs = new JobManager { OnJobFinished = metrics.RecordJobFinished },
            Candidates = new CandidateRegistry(),
            Launches = new Launching.LaunchRegistry(),
            Roots = new RootRegistry(),
            AuditLog = new Observability.AuditLogWriter(
                Path.Combine(resolved.CanonicalPath!, "logs")),
            Metrics = metrics,
            Events = events,
            Coordinator = null!,
            NotifyStopRequested = notifyStopRequested,
        };
        events.OnPublished = (coalesced, overflowed) => metrics.RecordEventPublished(coalesced, overflowed);
        WirePersistence(runtimeState);

        // settings（T-settings）：库就绪后读取持久化设置——激活视图恢复、核对周期生效。
        TimeSpan reconcileInterval = ScanCoordinator.DefaultInterval;
        if (library.Store is not null)
        {
            var settings = library.Store.ReadSettings();
            runtimeState.ActiveViewId = settings.ActiveViewId;
            reconcileInterval = TimeSpan.FromMinutes(settings.ScanIntervalMinutes);
        }

        runtimeState.Coordinator = new ScanCoordinator(
            runtimeState.Roots,
            events,
            rootPath => RunReconcileScan(runtimeState, rootPath),
            reconcileInterval);

        var logger = loggerFactory.CreateLogger<PipeServer>();
        var server = new PipeServer(
            ChannelNames.PipeName(resolved.ComparisonKey!),
            runtimeState,
            new OperationDispatcher(runtimeState),
            logger);
        server.Start();

        return new HostRuntime(guard, server, runtimeState);
    }

    /// <summary>
    /// 运行态持久化恢复与回调接线（v13+）：库根/Profile/启动历史回灌内存注册表；
    /// 上次运行未完成的作业标记 interrupted；此后注册表变更同步落库。
    /// </summary>
    private static void WirePersistence(HostRuntimeState state)
    {
        var store = state.Library.Store;
        if (store is null)
        {
            return;
        }

        // 1. 作业：中断标记先行（避免把上次崩溃时的 running 误当正常）。
        store.MarkInterruptedJobs(DateTime.UtcNow);

        // 2. 库根恢复。
        foreach (var persisted in store.ReadRoots())
        {
            try
            {
                state.Roots.AddExisting(persisted.RootId, persisted.PhysicalPath, persisted.CreatedUtc);
            }
            catch (RootRegistryException)
            {
                // 持久化路径已不合法（盘符移除等）：跳过，不阻断启动。
            }
        }

        // 3. 启动 Profile/历史恢复。
        foreach (var profile in store.ReadProfiles())
        {
            state.Launches.RestoreProfile(new Launching.LaunchProfile
            {
                ProfileId = profile.ProfileId,
                GameId = profile.GameId,
                ExecutablePath = profile.ExecutablePath,
                Arguments = profile.Arguments,
                WorkingDirectory = profile.WorkingDirectory,
                ToolId = profile.ToolId,
                IsDefault = profile.IsDefault,
                Revision = profile.Revision,
            });
        }

        foreach (var attempt in store.ReadLaunchAttempts())
        {
            state.Launches.RestoreAttempt(new Launching.LaunchAttempt
            {
                AttemptId = attempt.AttemptId,
                IdempotencyKey = attempt.IdempotencyKey,
                GameId = attempt.GameId,
                ProfileId = attempt.ProfileId,
                PlanId = attempt.PlanId,
                State = attempt.State,
                ExecutablePath = attempt.ExecutablePath,
                Arguments = attempt.Arguments,
                WorkingDirectory = attempt.WorkingDirectory,
                ProcessId = attempt.ProcessId,
                ProcessStartedUtc = attempt.ProcessStartedUtc,
                ExitCode = attempt.ExitCode,
                FinishedUtc = attempt.FinishedUtc,
                Error = attempt.Error,
                CreatedUtc = attempt.CreatedUtc,
            });
        }

        // 4. 变更回调接线（此后 create/update/finish 同步落库）。
        state.Launches.OnProfileChanged = profile =>
        {
            if (profile is null)
            {
                return;
            }

            store.UpsertProfile(new PersistedProfile(
                profile.ProfileId,
                profile.GameId,
                profile.ExecutablePath,
                profile.Arguments,
                profile.WorkingDirectory,
                profile.ToolId,
                profile.IsDefault,
                profile.Revision,
                DateTime.UtcNow,
                DateTime.UtcNow), DateTime.UtcNow);
        };
        state.Launches.OnAttemptChanged = attempt =>
            store.UpsertLaunchAttempt(new PersistedLaunchAttempt(
                attempt.AttemptId,
                attempt.IdempotencyKey,
                attempt.GameId,
                attempt.ProfileId,
                attempt.PlanId,
                attempt.State,
                attempt.ExecutablePath,
                attempt.Arguments,
                attempt.WorkingDirectory,
                attempt.ProcessId,
                attempt.ProcessStartedUtc,
                attempt.ExitCode,
                attempt.FinishedUtc,
                attempt.Error,
                attempt.CreatedUtc));
        state.Jobs.OnJobRecorded = snapshot =>
            store.UpsertJobRecord(new PersistedJobRecord(
                snapshot.JobId,
                snapshot.Kind,
                snapshot.State,
                snapshot.CreatedUtc,
                snapshot.StartedUtc,
                snapshot.FinishedUtc,
                snapshot.Error));
    }

    /// <summary>周期核对（T16）：小步重扫 + 候选落库/晋升 + 事件发布；与手动扫描共用同一路径。</summary>
    private static Hosting.JobOutcome RunReconcileScan(HostRuntimeState state, string rootPath)
    {
        var validation = GamePath.TryCreate(rootPath);
        if (!validation.IsValid)
        {
            return new Hosting.JobOutcome("failed", $"核对根路径非法：{rootPath}");
        }

        if (!Directory.Exists(validation.Path!.PhysicalPath))
        {
            return new Hosting.JobOutcome("failed", $"核对根离线：{rootPath}");
        }

        var jobId = $"job-reconcile-{Guid.NewGuid():N}";
        var collector = new Scanning.ScanCandidateCollector(validation.Path, jobId, state.Candidates);
        var context = new Hosting.JobContext { JobId = jobId, Token = CancellationToken.None };
        Infrastructure.Scanning.ScanCoverageData? completedCoverage = null;
        var outcome = Scanning.ScanJobRunner.Run(
            validation.Path,
            context,
            collector,
            onCompleted: coverage => completedCoverage = coverage,
            rules: Scanning.ScanIgnoreRuleSet.FromStore(state.Library.Store));
        if (outcome.FinalState == "succeeded")
        {
            Scanning.ScanCandidatePersistence.Persist(state.Library.Store, state.Events, collector, jobId);
            // T17：核对成功后同步库内游戏可用性（缺失/离线分级判定）。
            if (state.Library.Store is not null
                && completedCoverage?.Completion == Infrastructure.Scanning.ScanCompletion.Complete)
            {
                var report = Scanning.ReconcileService.CheckGames(state.Library.Store, DateTime.UtcNow);
                foreach (var transition in report.Transitions)
                {
                    state.Events.Publish("game.updated", $"game:{transition.GameId}", new
                    {
                        gameId = transition.GameId,
                        availability = transition.To,
                    }, DateTime.UtcNow);
                }
            }
        }

        return outcome;
    }

    private static async Task<HostLibraryState> OpenLibraryAsync(
        string canonicalDataDirectory,
        HostIdentity identity,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var options = new SqliteLibraryStoreOptions
        {
            AppVersion = identity.AppVersion,
            ApiVersion = ApiConstants.ApiVersion,
        };

        var result = await SqliteLibraryStore.TryOpenAsync(canonicalDataDirectory, options, ct);
        var logger = loggerFactory.CreateLogger<HostRuntime>();
        if (result.IsOpened)
        {
            logger.LogInformation(
                "库已打开：instance={LibraryInstanceId} epoch={DataEpoch} schema={SchemaVersion}",
                result.Store!.Info.LibraryInstanceId,
                result.Store.Info.DataEpoch,
                result.Store.Info.SchemaVersion);
            return new HostLibraryState
            {
                Status = result.Status,
                Store = result.Store,
                Detail = result.Detail,
            };
        }

        logger.LogWarning("库未就绪：{Status} {Detail}", result.Status, result.Detail);
        return HostLibraryState.NotInitialized(result.Status, result.Detail);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        if (_state.Library.Store is not null)
        {
            await _state.Library.Store.DisposeAsync();
        }

        _guard.Dispose();
    }
}

/// <summary>握手与 host.status 共用的宿主状态快照。</summary>
public sealed class HostRuntimeState
{
    public required HostIdentity Identity { get; init; }

    public required string DataDirectory { get; init; }

    /// <summary>library.init 在运行中打开库时整体替换。</summary>
    public required HostLibraryState Library { get; set; }

    public required JobManager Jobs { get; init; }

    /// <summary>扫描候选注册表（宿主内存态；T11 落库后由持久层承担）。</summary>
    public required CandidateRegistry Candidates { get; init; }

    /// <summary>启动 Profile/计划/尝试注册表（v13+ 持久化，重启恢复）。</summary>
    public required Launching.LaunchRegistry Launches { get; init; }

    /// <summary>库根白名单：扫描/启动路径包含边界（v13+ 持久化，重启恢复）。</summary>
    public required Scanning.RootRegistry Roots { get; init; }

    /// <summary>业务审计日志（T24）：JSONL 追加、轮转与保留期受控。</summary>
    public required Observability.AuditLogWriter AuditLog { get; init; }

    /// <summary>运行指标（T24-B）：请求延迟/错误码、事件计数、作业终态；经 diagnostics.status 暴露。</summary>
    public required Observability.HostMetrics Metrics { get; init; }

    /// <summary>库事件流（T16）：容量 4096、2s 折叠、游标增量读取；init/restore 后重绑 Store。</summary>
    public required Scanning.EventStream Events { get; init; }

    /// <summary>扫描协调器（T16）：周期核对、手动/后台互斥。构造后接线（依赖闭包）。</summary>
    public Scanning.ScanCoordinator Coordinator { get; set; } = null!;

    /// <summary>
    /// 维护模式（REC-02）：备份恢复期间为 true——新变更请求一律 MaintenanceMode 拒绝，
    /// 只允许只读与恢复自身；恢复完成或失败后退出。
    /// </summary>
    public volatile bool MaintenanceMode;

    /// <summary>
    /// 连接代数：library.init / backups.restore 成功后递增。PipeServer 在每次响应后
    /// 比对握手时的代数，不一致即断开旧客户端（恢复后旧纪元连接必须失效）。
    /// </summary>
    public int ConnectionGeneration;

    private volatile string? _activeViewId;

    /// <summary>当前激活视图（T15-C，宿主内存态；跨重启持久化随视图设置落库任务）。</summary>
    public string? ActiveViewId
    {
        get => _activeViewId;
        set => _activeViewId = value;
    }

    /// <summary>
    /// host.stop 停机回调（T18）：由 Program 接线到 IHostApplicationLifetime；
    /// dispatcher 在响应写出前调用，实现内部延迟触发以保证客户端先收到结果。
    /// </summary>
    public Action? NotifyStopRequested { get; init; }

    /// <summary>开机启动快捷方式管理（settings）：目录可注入（测试/绿色部署）；默认用户启动文件夹。</summary>
    public GameLibrary.Infrastructure.Shell.StartupShortcutManager StartupShortcuts { get; set; } =
        new();

    /// <summary>
    /// 库会话整体切换（v1 审查意见：LibrarySession 语义）——Library 状态与事件流
    /// 同步重绑到新 Store，并递增连接代数使旧纪元连接失效。init 与 restore 共用。
    /// </summary>
    public void BindLibraryStore(SqliteLibraryStore? store)
    {
        Library = new HostLibraryState
        {
            Status = store is null ? LibraryOpenStatus.NeedsInitialization : LibraryOpenStatus.Opened,
            Store = store,
            Detail = store is null ? "库已切换为未初始化" : "库已就绪",
        };
        Events.BindStore(store);
        Interlocked.Increment(ref ConnectionGeneration);
    }
}

/// <summary>LaunchProfile 的创建时间取值辅助（持久化用）。</summary>
internal static class LaunchRegistryPersistenceExtensions
{
    public static DateTime CreatedUtc(this Launching.LaunchProfile profile) => DateTime.MinValue + (DateTime.UtcNow - DateTime.UtcNow);
}
