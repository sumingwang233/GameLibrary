using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Review;

public sealed class GameRemovalScopeTests(PipeServerFixture fixture) : IClassFixture<PipeServerFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyResourceOnlyCandidate_CannotBeAcceptedAsAGame(bool directoryEntry)
    {
        var root = Tree();
        File.WriteAllText(Path.Combine(root, "patch.xp3"), "resource only");
        var id = Guid.NewGuid().ToString();
        var store = fixture.State.Library.Store!;
        store.UpsertCandidate(new PersistedCandidate
        {
            CandidateId = id,
            JobId = "legacy",
            PhysicalPath = root,
            RelativePath = "patch-only",
            Kind = "nestedCandidate",
            ReviewState = "pendingReview",
            Revision = 1,
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                engines = new[] { new { engine = "kirikiri" } },
                entryCandidates = directoryEntry ? new[] { new { relativePath = root } } : []
            }, ContractJson.Options)
        });
        var before = store.ListGames().Count;
        var accepted = await Invoke("candidates.accept", new { candidateId = id, expectedRevision = 1, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.False(accepted.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, accepted.Error!.Code);
        Assert.Equal(before, store.ListGames().Count);
        Assert.Equal("pendingReview", store.TryGetCandidate(id)!.ReviewState);
        Assert.True(File.Exists(Path.Combine(root, "patch.xp3")));
    }

    private async Task<Envelope<JsonElement>> Invoke(string operation, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(fixture.State.DataDirectory, "removal-scope-test", CancellationToken.None);
        return await client.InvokeAsync(new IpcRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            OperationId = operation,
            Parameters = JsonSerializer.SerializeToElement(parameters, ContractJson.Options)
        }, CancellationToken.None);
    }

    private string Tree()
    {
        var root = Path.Combine(fixture.State.DataDirectory, "scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ManualExe_PreviewsWholeProject_RecyclesResources_AndReplaysWithoutTouchingSibling()
    {
        var root = Tree();
        var directory = Path.Combine(root, "Game");
        Directory.CreateDirectory(Path.Combine(directory, "Game_Data"));
        File.WriteAllText(Path.Combine(directory, "Game.exe"), "never execute");
        File.WriteAllText(Path.Combine(directory, "UnityPlayer.dll"), "never execute");
        File.WriteAllText(Path.Combine(directory, "Game_Data", "globalgamemanagers"), "test");
        File.WriteAllText(Path.Combine(directory, "Game_Data", "save.dat"), "keep within project scope");
        File.WriteAllText(Path.Combine(root, "neighbor.txt"), "keep");
        Assert.True((await Invoke("roots.add", new { root = directory, kind = "manual" })).Ok);
        var created = await Invoke("games.create", new { sourcePath = Path.Combine(directory, "Game.exe"), idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(created.Ok, created.Error?.Message);
        var gameId = created.Data.GetProperty("gameId").GetString()!;
        var detail = await Invoke("games.get", new { gameId, includeRemovalPlan = true });
        Assert.True(detail.Ok, detail.Error?.Message);
        var plan = detail.Data.GetProperty("removalPlan");
        Assert.Equal(directory, plan.GetProperty("targetPath").GetString());
        Assert.Equal("directory", plan.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("error").ValueKind);
        var revision = detail.Data.GetProperty("revision").GetInt32();
        var wrongScope = await Invoke("games.remove", new
        {
            gameId,
            expectedRevision = revision,
            deleteFiles = true,
            confirmedPath = Path.Combine(directory, "Game.exe"),
            idempotencyKey = Guid.NewGuid().ToString()
        });
        Assert.False(wrongScope.Ok);
        Assert.True(File.Exists(Path.Combine(directory, "Game.exe")));
        var request = new { gameId, expectedRevision = revision, deleteFiles = true, confirmedPath = directory, idempotencyKey = Guid.NewGuid().ToString() };
        var removed = await Invoke("games.remove", request);
        Assert.True(removed.Ok, removed.Error?.Message);
        Assert.Equal(directory, removed.Data.GetProperty("recycledPath").GetString());
        Assert.False(Directory.Exists(directory));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "neighbor.txt")));
        Assert.True((await Invoke("games.remove", request)).Ok);
    }

    [Fact]
    public async Task CollectionSwf_RecyclesOnlySelectedFile_AndRefusesAReplacedDirectory()
    {
        var root = Tree();
        Assert.True((await Invoke("roots.add", new { root })).Ok);
        var first = Path.Combine(root, "one.swf");
        var second = Path.Combine(root, "two.swf");
        File.WriteAllText(first, "isolated swf");
        File.WriteAllText(second, "keep");
        var id = Guid.NewGuid().ToString();
        fixture.State.Library.Store!.InsertGame(new GameCard
        {
            GameId = id,
            Title = "one",
            RootPath = first,
            Kind = "fileGame",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        var detail = await Invoke("games.get", new { gameId = id, includeRemovalPlan = true });
        Assert.Equal("file", detail.Data.GetProperty("removalPlan").GetProperty("scope").GetString());
        File.Move(first, Path.Combine(root, "original.swf"));
        Directory.CreateDirectory(first);
        var replaced = await Invoke("games.remove", new { gameId = id, expectedRevision = 1, deleteFiles = true, confirmedPath = first, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.False(replaced.Ok);
        Directory.Move(first, Path.Combine(root, "replacement-directory"));
        File.Move(Path.Combine(root, "original.swf"), first);
        var removed = await Invoke("games.remove", new { gameId = id, expectedRevision = 1, deleteFiles = true, confirmedPath = first, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(removed.Ok, removed.Error?.Message);
        Assert.False(File.Exists(first));
        Assert.Equal("keep", File.ReadAllText(second));
        Assert.True(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualExe_DoesNotExpandIntoSharedOrLibraryDirectory(bool libraryRoot)
    {
        var root = Tree();
        File.WriteAllText(Path.Combine(root, "Game.exe"), "never execute");
        File.WriteAllText(Path.Combine(root, "Other.exe"), "never execute");
        Assert.True((await Invoke("roots.add", new { root, kind = libraryRoot ? "library" : "manual" })).Ok);
        var created = await Invoke("games.create", new { sourcePath = Path.Combine(root, "Game.exe"), idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(created.Ok, created.Error?.Message);
        var gameId = created.Data.GetProperty("gameId").GetString()!;
        var detail = await Invoke("games.get", new { gameId, includeRemovalPlan = true });
        Assert.NotEqual(JsonValueKind.Null, detail.Data.GetProperty("removalPlan").GetProperty("error").ValueKind);
        var removed = await Invoke("games.remove", new { gameId, expectedRevision = 1, deleteFiles = true, confirmedPath = root, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.False(removed.Ok);
        Assert.True(File.Exists(Path.Combine(root, "Game.exe")));
        Assert.True(File.Exists(Path.Combine(root, "Other.exe")));
    }
}
