using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Ipc;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host;

/// <summary>
/// 宿主运行时装配：数据目录守卫 + 管道服务。库初始化（library.init）前不打开数据库。
/// </summary>
public sealed class HostRuntime : IAsyncDisposable
{
    private readonly SingleInstanceGuard _guard;
    private readonly PipeServer _server;

    private HostRuntime(SingleInstanceGuard guard, PipeServer server)
    {
        _guard = guard;
        _server = server;
    }

    public HostIdentity Identity { get; private init; } = null!;

    public string DataDirectory { get; private init; } = "";

    public static HostRuntime Start(string dataDirectory, ILoggerFactory loggerFactory)
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
        var logger = loggerFactory.CreateLogger<PipeServer>();
        var server = new PipeServer(
            ChannelNames.PipeName(resolved.ComparisonKey!),
            identity,
            new OperationDispatcher(identity),
            logger);
        server.Start();

        return new HostRuntime(guard, server)
        {
            Identity = identity,
            DataDirectory = resolved.CanonicalPath!,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _guard.Dispose();
    }
}
