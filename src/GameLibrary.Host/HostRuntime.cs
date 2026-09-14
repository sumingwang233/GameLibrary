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
        var outcome = Scanning.ScanJobRunner.Run(validation.Path, context, collector);
        if (outcome.FinalState == "succeeded")
        {
            Scanning.ScanCandidatePersistence.Persist(state.Library.Store, state.Events, collector, jobId);
            // T17：核对成功后同步库内游戏可用性（缺失/离线分级判定）。
            if (state.Library.Store is not null)
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

    /// <summary>启动 Profile/计划/尝试注册表（宿主内存态；收据持久化随 T23/T27）。</summary>
    public required Launching.LaunchRegistry Launches { get; init; }

    /// <summary>库根白名单：扫描/启动路径包含边界（宿主内存态；持久化随 T16）。</summary>
    public required Scanning.RootRegistry Roots { get; init; }

    /// <summary>业务审计日志（T24）：JSONL 追加、轮转与保留期受控。</summary>
    public required Observability.AuditLogWriter AuditLog { get; init; }

    /// <summary>运行指标（T24-B）：请求延迟/错误码、事件计数、作业终态；经 diagnostics.status 暴露。</summary>
    public required Observability.HostMetrics Metrics { get; init; }

    /// <summary>库事件流（T16）：容量 4096、2s 折叠、游标增量读取。</summary>
    public required Scanning.EventStream Events { get; init; }

    /// <summary>扫描协调器（T16）：周期核对、手动/后台互斥。构造后接线（依赖闭包）。</summary>
    public Scanning.ScanCoordinator Coordinator { get; set; } = null!;

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
}
