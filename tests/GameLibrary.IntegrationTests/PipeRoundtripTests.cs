using GameLibrary.Contracts.Ipc;
using GameLibrary.Host;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;


namespace GameLibrary.IntegrationTests;

/// <summary>进程内管道回环：真实 NamedPipeServerStream + HostClient，不起 Host 进程。</summary>
public sealed class PipeRoundtripTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public PipeRoundtripTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Connect_CompletesHandshakeWithInstanceInfo()
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "contract-test",
            CancellationToken.None);

        Assert.Equal("1", client.Handshake.ApiVersion);
        Assert.Equal(_fixture.Identity.InstanceId, client.Handshake.HostInstanceId);
        // T23-A 起夹具直接建库（收据中间件需要库实例）。
        Assert.True(client.Handshake.LibraryInitialized);
        Assert.NotNull(client.Handshake.LibraryInstanceId);
        Assert.NotEmpty(client.Handshake.AppVersion);
    }

    [Fact]
    public async Task Invoke_HostStatus_ReturnsCompletedEnvelope()
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: null,
            CancellationToken.None);

        var envelope = await client.InvokeAsync(
            new Contracts.Ipc.IpcRequest { RequestId = "req-status-1", OperationId = "host.status" },
            CancellationToken.None);

        Assert.True(envelope.Ok);
        Assert.Equal(Contracts.OperationStatus.Completed, envelope.Status);
        Assert.Equal("req-status-1", envelope.RequestId);
        Assert.Equal(_fixture.Identity.InstanceId, envelope.Data.GetProperty("hostInstanceId").GetString());
        Assert.Equal("1", envelope.Data.GetProperty("apiVersion").GetString());
    }

    [Fact]
    public async Task Invoke_UnimplementedOperation_ReturnsUnsupportedOperation()
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: null,
            CancellationToken.None);

        var envelope = await client.InvokeAsync(
            new Contracts.Ipc.IpcRequest { RequestId = "req-x-1", OperationId = "games.update" },
            CancellationToken.None);

        Assert.False(envelope.Ok);
        Assert.Equal(Contracts.OperationStatus.Failed, envelope.Status);
        Assert.Equal(Contracts.ErrorCodes.UnsupportedOperation, envelope.Error!.Code);
    }

    [Fact]
    public async Task TwoClients_CanConnectAndInvokeConcurrently()
    {
        var dataDir = @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data";
        await using var client1 = await HostConnection.ConnectAsync(dataDir, "c1", CancellationToken.None);
        await using var client2 = await HostConnection.ConnectAsync(dataDir, "c2", CancellationToken.None);

        var results = await Task.WhenAll(
            client1.InvokeAsync(new Contracts.Ipc.IpcRequest { RequestId = "r1", OperationId = "host.status" }, CancellationToken.None),
            client2.InvokeAsync(new Contracts.Ipc.IpcRequest { RequestId = "r2", OperationId = "host.status" }, CancellationToken.None));

        Assert.True(results[0].Ok);
        Assert.True(results[1].Ok);
        Assert.Equal("r1", results[0].RequestId);
        Assert.Equal("r2", results[1].RequestId);
    }
}

public sealed class PipeServerFixture : IAsyncDisposable
{
    public PipeServerFixture()
    {
        TestId = Guid.NewGuid().ToString("N");
        Identity = new HostIdentity();
        var dataDir = DataDirectory.Resolve(@$"D:\Official\GameLibrary\artifacts\test-runs\{TestId}\data");
        Assert.True(dataDir.IsValid);
        // T23-A：收据中间件需要库实例；夹具直接建库（library.init 语义的等价准备步骤）。
        var init = GameLibrary.Infrastructure.Persistence.SqliteLibraryStore.InitializeAsync(
            dataDir.CanonicalPath!,
            new GameLibrary.Infrastructure.Persistence.SqliteLibraryStoreOptions
            {
                AppVersion = Identity.AppVersion,
                ApiVersion = GameLibrary.Contracts.ApiConstants.ApiVersion,
            },
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(init.IsOpened, init.Detail);
        State = new HostRuntimeState
        {
            Identity = Identity,
            DataDirectory = dataDir.CanonicalPath!,
            Library = new HostLibraryState
            {
                Status = GameLibrary.Infrastructure.Persistence.LibraryOpenStatus.Opened,
                Store = init.Store,
            },
            Jobs = new JobManager(),
            Candidates = new GameLibrary.Host.Scanning.CandidateRegistry(),
            Launches = new GameLibrary.Host.Launching.LaunchRegistry(),
            Roots = new GameLibrary.Host.Scanning.RootRegistry(),
            AuditLog = new GameLibrary.Host.Observability.AuditLogWriter(
                System.IO.Path.Combine(dataDir.CanonicalPath!, "logs")),
        };
        Server = new PipeServer(
            ChannelNames.PipeName(dataDir.ComparisonKey!),
            State,
            new OperationDispatcher(State),
            NullLogger.Instance);
        Server.Start();
    }

    public string TestId { get; }

    public HostIdentity Identity { get; }

    public HostRuntimeState State { get; }

    public PipeServer Server { get; }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        var testRoot = @$"D:\Official\GameLibrary\artifacts\test-runs\{TestId}";
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
