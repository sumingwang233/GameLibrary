using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Ipc;
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
    private readonly SqliteLibraryStore? _store;

    private HostRuntime(SingleInstanceGuard guard, PipeServer server, SqliteLibraryStore? store)
    {
        _guard = guard;
        _server = server;
        _store = store;
    }

    public HostIdentity Identity { get; private init; } = null!;

    public HostLibraryState Library { get; private init; } = null!;

    public string DataDirectory { get; private init; } = "";

    public static async Task<HostRuntime> StartAsync(
        string dataDirectory,
        ILoggerFactory loggerFactory,
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
        var runtimeState = new HostRuntimeState
        {
            Identity = identity,
            Library = library,
            Jobs = new JobManager(),
        };

        var logger = loggerFactory.CreateLogger<PipeServer>();
        var server = new PipeServer(
            ChannelNames.PipeName(resolved.ComparisonKey!),
            runtimeState,
            new OperationDispatcher(runtimeState),
            logger);
        server.Start();

        return new HostRuntime(guard, server, library.Store)
        {
            Identity = identity,
            Library = library,
            DataDirectory = resolved.CanonicalPath!,
        };
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
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _guard.Dispose();
    }
}

/// <summary>握手与 host.status 共用的宿主状态快照。</summary>
public sealed class HostRuntimeState
{
    public required HostIdentity Identity { get; init; }

    public required HostLibraryState Library { get; init; }

    public required JobManager Jobs { get; init; }
}
