using System.IO.Pipes;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Launching;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Backups;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests;

// Source-only regressions for v1.7.5; execution requires the maintainer's separate authorization.
public sealed class ArchitectureBoundariesTests
{
    [Fact]
    public void GeneratedMcpSchemaSupportsOmittedTagPatchAndCropCoordinates()
    {
        var update = ModelContextProtocol.Server.McpServerTool.Create(
            typeof(GameLibrary.Mcp.GameLibraryTools).GetMethod("TagsUpdate")!).ProtocolTool.InputSchema;
        var required = update.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.DoesNotContain("color", required);
        Assert.DoesNotContain("displayName", required);
        Assert.Equal(JsonValueKind.Null, update.GetProperty("properties").GetProperty("color").GetProperty("default").ValueKind);
        var crop = ModelContextProtocol.Server.McpServerTool.Create(
            typeof(GameLibrary.Mcp.GameLibraryTools).GetMethod("AssetsCrop")!).ProtocolTool.InputSchema;
        foreach (var field in new[] { "gameId", "assetId", "x", "y", "width", "height", "expectedRevision", "idempotencyKey" })
            Assert.Contains(crop.GetProperty("required").EnumerateArray(), value => value.GetString() == field);
    }

    [Fact]
    public async Task ReconnectRetainsOriginalWriteEpoch()
    {
        var data = Path.Combine(Path.GetTempPath(), "gamelibrary-reconnect-" + Guid.NewGuid().ToString("N"));
        var resolved = DataDirectory.Resolve(data);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var requests = new List<IpcRequest>();
        var server = Task.Run(async () =>
        {
            for (var pass = 0; pass < 2; pass++)
            {
                await using var pipe = new NamedPipeServerStream(ChannelNames.PipeName(resolved.ComparisonKey!),
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(timeout.Token);
                _ = await IpcFrame.ReadJsonAsync<HandshakeRequest>(pipe, timeout.Token);
                await IpcFrame.WriteJsonAsync(pipe, new HandshakeResponse
                {
                    HostInstanceId = "host-" + pass, LibraryInstanceId = "library",
                    DataEpoch = pass == 0 ? "original" : "restored", LibraryInitialized = true,
                }, timeout.Token);
                var request = await IpcFrame.ReadJsonAsync<IpcRequest>(pipe, timeout.Token);
                requests.Add(request);
                if (pass == 1)
                    await IpcFrame.WriteJsonAsync(pipe, new Envelope<object>
                    {
                        RequestId = request.RequestId, Ok = false, Status = OperationStatus.Failed,
                        Error = new RequestError { Code = ErrorCodes.DataEpochMismatch, Message = "stale", Retryable = false },
                    }, timeout.Token);
                // First connection closes after receiving the write, before sending its response.
            }
        }, timeout.Token);
        await using var client = await HostConnection.ConnectAsync(data, "boundary-test", timeout.Token);
        var result = await client.InvokeAsync(new IpcRequest
        {
            RequestId = "write", OperationId = "tags.create",
            Parameters = JsonSerializer.SerializeToElement(new { name = "tag", idempotencyKey = "intent" }, ContractJson.Options),
        }, timeout.Token);
        await server;
        Assert.Equal(ErrorCodes.DataEpochMismatch, result.Error?.Code);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal("original", request.ExpectedDataEpoch));
    }

    [Fact]
    public void PreparedLaunchAssociatesBeforeSideEffectAndUsesActorScope()
    {
        var registry = new LaunchRegistry();
        var executable = Environment.ProcessPath!;
        var workingDirectory = Path.GetDirectoryName(executable)!;
        var first = registry.AddProfile("first", executable, [], workingDirectory);
        var second = registry.AddProfile("second", executable, [], workingDirectory);
        var a = registry.Prepare("same-key", null, first.ProfileId, null, null,
            RequestReceiptStore.ScopeKey("library", "actor-a", "launch.execute", "same-key"), prepared =>
            {
                Assert.Equal("prepared", prepared.State);
                Assert.Null(prepared.ProcessId);
            });
        var b = registry.Prepare("same-key", null, second.ProfileId, null, null,
            RequestReceiptStore.ScopeKey("library", "actor-b", "launch.execute", "same-key"));
        Assert.NotEqual(a.AttemptId, b.AttemptId);
        registry.FailPrepared(a.AttemptId, "not scheduled");
        registry.FailPrepared(b.AttemptId, "not scheduled");
        var restarted = new LaunchRegistry();
        var scope = RequestReceiptStore.ScopeKey("library", "actor-a", "launch.execute", "same-key");
        restarted.RestoreAttempt(a, scope);
        var replay = restarted.Execute("same-key", null, null, null, null, receiptScope: scope);
        Assert.Equal(a.AttemptId, replay.AttemptId);
        Assert.Equal("unknownOutcome", replay.State);
        Assert.Null(replay.ProcessId);
    }

    [Fact]
    public async Task DatabaseMutationAndReceiptRollBackTogether()
    {
        await using var fixture = new PipeServerFixture();
        var store = fixture.State.Library.Store!;
        var before = store.ReadSettings();
        var receipt = Receipt(store, "settings.update", "rollback");
        Assert.Throws<IOException>(() => store.InTransaction<int>(() =>
        {
            store.InsertPreparedReceipt(receipt);
            store.WriteSettingsKeys([("theme", before.Theme == "dark" ? "light" : "dark")], DateTime.UtcNow);
            store.CompleteReceipt(receipt, "{}");
            throw new IOException("interrupted commit");
        }));
        Assert.Null(store.TryGetReceipt(receipt.Actor, receipt.OperationId, receipt.IdempotencyKey));
        Assert.Equal(before.Theme, store.ReadSettings().Theme);
        Assert.Equal(before.Revision, store.ReadSettings().Revision);
    }

    [Fact]
    public async Task BackupRejectsDatabaseAssetMissingFromOtherwiseValidManifest()
    {
        await using var fixture = new PipeServerFixture();
        var store = fixture.State.Library.Store!;
        var game = Game(fixture.State.DataDirectory);
        store.InsertGame(game);
        var owned = Path.Combine(fixture.State.DataDirectory, "assets", game.GameId, "cover.png");
        Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
        File.WriteAllBytes(owned, [1, 2, 3]);
        store.ImportAsset(game.GameId, owned, DateTime.UtcNow);
        var storage = new LibraryBackupStorage(store, fixture.State.DataDirectory, "backup-boundary");
        await storage.CreateSnapshotAsync(CancellationToken.None);
        var directory = BackupArchive.BackupDirectory(Path.Combine(fixture.State.DataDirectory, "backups"), "backup-boundary");
        File.Delete(Path.Combine(directory, "assets", game.GameId, "cover.png"));
        _ = storage.CommitManifest(storage.CopyAssets());
        var manifest = BackupArchive.TryReadManifest(directory)!;
        Assert.Contains(BackupArchive.Verify(directory, manifest), problem => problem.Contains("资产缺失", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecycleRecoveryCommitsOnlyWhenTargetAbsenceAndRevisionAreProven(bool changedRevision)
    {
        await using var fixture = new PipeServerFixture();
        var store = fixture.State.Library.Store!;
        var game = Game(fixture.State.DataDirectory);
        store.InsertGame(game);
        var receipt = Receipt(store, "games.remove", "recycle") with
        {
            AttemptJson = JsonSerializer.Serialize(new
            {
                kind = "recycle", gameId = game.GameId, target = game.RootPath,
                expectedRevision = game.Revision, dataEpoch = store.Info.DataEpoch, phase = "moving",
            }, ContractJson.Options),
        };
        store.InsertPreparedReceipt(receipt);
        if (changedRevision) store.SetFavorite(game.GameId, true, game.Revision, DateTime.UtcNow);
        var handler = new GamesHandler(() => store, fixture.State.Roots, fixture.State.Events);
        var result = handler.ResumeRemoval(new IpcRequest
        {
            RequestId = "resume", OperationId = "games.remove", ClientName = receipt.Actor,
        }, receipt)!;
        Assert.Equal(!changedRevision, result.Ok);
        Assert.Equal(changedRevision ? "active" : "removed", store.TryGetGame(game.GameId)!.Membership);
        Assert.Equal(changedRevision ? "prepared" : "completed",
            store.TryGetReceipt(receipt.Actor, receipt.OperationId, receipt.IdempotencyKey)!.Status);
        Assert.False(File.Exists(game.RootPath));
    }

    [Fact]
    public async Task CancelledPreparedLaunchNeverCreatesProcessAndReleasesGame()
    {
        var registry = new LaunchRegistry();
        var executable = Environment.ProcessPath!;
        var profile = registry.AddProfile("cancelled-game", executable, [], Path.GetDirectoryName(executable)!);
        var prepared = registry.Prepare("cancel", null, profile.ProfileId, null, null);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Throws<LaunchException>(() => registry.RunPrepared(prepared.AttemptId, cancellationToken: cancelled.Token));
        Assert.Null(registry.GetAttempt(prepared.AttemptId)!.ProcessId);
        Assert.Equal("processStartFailed", registry.GetAttempt(prepared.AttemptId)!.State);
        var next = registry.Prepare("new-intent", null, profile.ProfileId, null, null);
        registry.FailPrepared(next.AttemptId, "test finished without launching");
    }

    private static GameCard Game(string parent) => new()
    {
        GameId = "game-" + Guid.NewGuid().ToString("N"), Title = "Boundary sample",
        RootPath = Path.Combine(parent, "absent-game.exe"), Kind = "fileGame", Membership = "active",
        AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };

    private static RequestReceipt Receipt(SqliteLibraryStore store, string operation, string key) => new()
    {
        LibraryInstanceId = store.Info.LibraryInstanceId, Actor = "boundary-test", OperationId = operation,
        IdempotencyKey = key, RequestDigest = "boundary", Status = "prepared",
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };
}
