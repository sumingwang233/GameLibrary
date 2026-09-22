using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Manual;

/// <summary>
/// bug-5 manual 根语义（经真实管道）：roots.add kind=manual 合法注册；
/// manual 根满足路径包含校验（games.create 可用）；roots.list 默认不返回、
/// includeManual=true 返回（带 kind 字段）；manual 根不进扫描枚举（ListScannable）；
/// manual 根下的候选不落库（UpsertRegisteredCandidate 门控，含嵌套在 library 根内的情形）。
/// </summary>
public sealed class RootsKindTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public RootsKindTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static string StubExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    private static string NewRunDir(string prefix) =>
        Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "roots-kind-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters);
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task RootsAdd_ManualKind_RegistersButDefaultListHidesIt()
    {
        var libraryDir = NewRunDir("roots-library");
        Directory.CreateDirectory(libraryDir);
        var manualDir = NewRunDir("roots-manual");
        Directory.CreateDirectory(manualDir);

        // 默认 kind=library 的注册照旧。
        var libraryAdded = await InvokeAsync("roots.add", new { root = libraryDir });
        Assert.True(libraryAdded.Ok, libraryAdded.Error?.Message);
        Assert.Equal("library", libraryAdded.Data.GetProperty("kind").GetString());

        var added = await InvokeAsync("roots.add", new { root = manualDir, kind = "manual" });
        Assert.True(added.Ok, added.Error?.Message);
        Assert.Equal("manual", added.Data.GetProperty("kind").GetString());

        // 默认 roots.list 不返回 manual 根（既有消费方看到的仍是扫描边界）。
        var defaultList = await InvokeAsync("roots.list", new { });
        Assert.True(defaultList.Ok);
        Assert.DoesNotContain(defaultList.Data.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("path").GetString() == manualDir);
        Assert.Contains(defaultList.Data.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("path").GetString() == libraryDir);

        // includeManual=true 返回全部（DTO 带 kind 字段）。
        var fullList = await InvokeAsync("roots.list", new { includeManual = true });
        Assert.True(fullList.Ok);
        var item = fullList.Data.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("path").GetString() == manualDir);
        Assert.Equal("manual", item.GetProperty("kind").GetString());
        Assert.Contains(fullList.Data.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("path").GetString() == libraryDir && i.GetProperty("kind").GetString() == "library");

        // manual 根不进扫描枚举（周期核对从这里取根），library 根进。
        Assert.DoesNotContain(_fixture.State.Roots.ListScannable(), r => r.Path.PhysicalPath == manualDir);
        Assert.Contains(_fixture.State.Roots.ListScannable(), r => r.Path.PhysicalPath == libraryDir);
        Assert.Contains(_fixture.State.Roots.List(), r => r.Path.PhysicalPath == manualDir);
    }

    [Fact]
    public async Task RootsAdd_UnknownKind_IsRejected()
    {
        var dir = NewRunDir("roots-badkind");
        Directory.CreateDirectory(dir);

        var rejected = await InvokeAsync("roots.add", new { root = dir, kind = "scanner" });
        Assert.False(rejected.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, rejected.Error!.Code);
    }

    [Fact]
    public async Task GamesCreate_UnderManualRootOnly_PassesContainment()
    {
        // 目录不在任何 library 根内——只有 manual 根覆盖时 games.create 仍合法（bug-5 场景）。
        var manualDir = NewRunDir("roots-manual-create");
        Directory.CreateDirectory(manualDir);
        var exePath = Path.Combine(manualDir, "Standalone.exe");
        File.Copy(StubExe, exePath);

        var added = await InvokeAsync("roots.add", new { root = manualDir, kind = "manual" });
        Assert.True(added.Ok, added.Error?.Message);

        var created = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"create-manual-{Guid.NewGuid():N}",
            sourcePath = exePath,
            title = "manual 根下手动添加",
        });
        Assert.True(created.Ok, created.Error?.Message);
        Assert.Equal("manualFile", created.Data.GetProperty("kind").GetString());
    }

    [Fact]
    public void UpsertRegisteredCandidate_ManualRootCandidatesAreNotStored()
    {
        var store = _fixture.State.Library.Store!;
        var utcNow = DateTime.UtcNow;

        // library 根 C:\lib-t + manual 根（嵌套在 library 根内）C:\lib-t\manual-area + 独立 manual 根。
        store.UpsertRoot(new GameLibrary.Infrastructure.Persistence.PersistedRoot(
            "root-kindtest-lib", @"C:\lib-t", 1, utcNow, "library"), utcNow);
        store.UpsertRoot(new GameLibrary.Infrastructure.Persistence.PersistedRoot(
            "root-kindtest-manual-nested", @"C:\lib-t\manual-area", 1, utcNow, "manual"), utcNow);
        store.UpsertRoot(new GameLibrary.Infrastructure.Persistence.PersistedRoot(
            "root-kindtest-manual-solo", @"C:\solo-manual", 1, utcNow, "manual"), utcNow);

        // library 根下的普通候选：正常落库。
        Assert.True(store.UpsertRegisteredCandidate(Candidate(@"C:\lib-t\GameA")).Stored);
        Assert.True(store.ReadExclusive((c, _) =>
            GameLibrary.Infrastructure.Persistence.LibraryCatalogStore.CandidateExists(c, @"C:\lib-t\GameA")));

        // manual 根下的候选：不落库（即使其外层还有 library 根覆盖——手动添加的位置不再报候选）。
        Assert.False(store.UpsertRegisteredCandidate(Candidate(@"C:\lib-t\manual-area\GameB")).Stored);
        Assert.False(store.UpsertRegisteredCandidate(Candidate(@"C:\solo-manual\GameC")).Stored);
        Assert.False(store.ReadExclusive((c, _) =>
            GameLibrary.Infrastructure.Persistence.LibraryCatalogStore.CandidateExists(c, @"C:\lib-t\manual-area\GameB")));
        Assert.False(store.ReadExclusive((c, _) =>
            GameLibrary.Infrastructure.Persistence.LibraryCatalogStore.CandidateExists(c, @"C:\solo-manual\GameC")));

        // 门控只针对候选发现：manual 根本身仍是合法路径包含边界。
        Assert.True(GameLibrary.Infrastructure.Persistence.RuntimeStateStore.ContainsPath(
            @"C:\solo-manual", @"C:\solo-manual\GameC\inner\file.exe"));
    }

    private static GameLibrary.Infrastructure.Persistence.PersistedCandidate Candidate(string physicalPath) => new()
    {
        CandidateId = $"cand-{Guid.NewGuid():N}",
        JobId = "job-kindtest",
        Kind = "gameRoot",
        RelativePath = Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar)),
        PhysicalPath = physicalPath,
        PayloadJson = "{}",
        ReviewState = "observed",
        ObservedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };
}
