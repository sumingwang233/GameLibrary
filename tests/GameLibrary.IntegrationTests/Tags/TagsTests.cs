using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Tags;

/// <summary>
/// T-collections tags.×8（阶段三，经真实管道）：入库自动创建引擎标签、
/// 用户标签 CRUD、assign/unassign、删除自动标签登记 Suppress、reset 恢复、
/// games.list tagId 数据库侧过滤、Revision 冲突。
/// </summary>
public sealed class TagsTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public TagsTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "tags-test",
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

    private static string CreateGameTree(string prefix, string gameName = "GameA")
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, gameName));
        File.WriteAllText(Path.Combine(path, gameName, "Game.exe"), "x");
        File.WriteAllText(Path.Combine(path, gameName, "data.xp3"), "x");
        return path;
    }

    private async Task ScanAndWaitAsync(string root)
    {
        var start = await InvokeAsync("scan.start", new { idempotencyKey = "scan-" + Guid.NewGuid().ToString("N"), root });
        Assert.True(start.Ok, start.Error?.Message);
        var jobId = start.JobId!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await InvokeAsync("jobs.get", new { jobId });
            var state = status.Data.GetProperty("state").GetString();
            if (state is "succeeded" or "failed")
            {
                Assert.Equal("succeeded", state);
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"扫描作业 {jobId} 未在 15 秒内完成");
    }

    /// <summary>入库一个游戏（手动扫描 → pendingReview → accept），返回 (gameId, revision)。</summary>
    private async Task<(string GameId, int Revision)> CreateGameAsync(string prefix)
    {
        var root = CreateGameTree(prefix);
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);

        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("kind").GetString() == "gameRoot"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var accept = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = $"accept-{item.GetProperty("candidateId").GetString()}",
            candidateId = item.GetProperty("candidateId").GetString(),
            expectedRevision = item.GetProperty("revision").GetInt32(),
        });
        Assert.True(accept.Ok, accept.Error?.Message);
        var gameId = accept.Data.GetProperty("gameId").GetString()!;
        var created = await InvokeAsync("games.get", new { gameId });
        return (gameId, created.Data.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task TagCreate_WithNewFields_ReturnsDefaultsAndProvidedValues()
    {
        // feat-3：显式提供四新字段。
        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "联机合作",
            color = "#FFB86C",
            category = "social",
            sortOrder = 7,
            starred = true,
            displayName = "联机·合作",
        });
        Assert.True(create.Ok, create.Error?.Message);
        var tagId = create.Data.GetProperty("tagId").GetString()!;
        Assert.Equal("social", create.Data.GetProperty("category").GetString());
        Assert.Equal(7, create.Data.GetProperty("sortOrder").GetInt32());
        Assert.True(create.Data.GetProperty("starred").GetBoolean());
        Assert.Equal("联机·合作", create.Data.GetProperty("displayName").GetString());

        // 缺省值：category 按 kind 推断（user→special）、sortOrder=0、starred=false、displayName=null。
        var plain = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "普通标签",
        });
        Assert.True(plain.Ok, plain.Error?.Message);
        Assert.Equal("special", plain.Data.GetProperty("category").GetString());
        Assert.Equal(0, plain.Data.GetProperty("sortOrder").GetInt32());
        Assert.False(plain.Data.GetProperty("starred").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plain.Data.GetProperty("displayName").ValueKind);

        // tags.list 返回全部新字段。
        var list = await InvokeAsync("tags.list", new { });
        var listed = list.Data.GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("tagId").GetString() == tagId);
        Assert.Equal("social", listed.GetProperty("category").GetString());
        Assert.Equal(7, listed.GetProperty("sortOrder").GetInt32());
        Assert.True(listed.GetProperty("starred").GetBoolean());
        Assert.Equal("联机·合作", listed.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TagCreateOrUpdate_InvalidCategory_IsRejected()
    {
        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "坏分类标签",
            category = "mood",
        });
        Assert.False(create.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, create.Error!.Code);

        var seed = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "好分类标签",
        });
        Assert.True(seed.Ok, seed.Error?.Message);
        var update = await InvokeAsync("tags.update", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            tagId = seed.Data.GetProperty("tagId").GetString(),
            category = "vibe",
            expectedRevision = seed.Data.GetProperty("revision").GetInt32(),
        });
        Assert.False(update.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, update.Error!.Code);
    }

    [Fact]
    public async Task TagUpdate_PatchesNewFields_AndCanClearDisplayName()
    {
        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "待分类标签",
        });
        Assert.True(create.Ok, create.Error?.Message);
        var tagId = create.Data.GetProperty("tagId").GetString()!;
        var revision = create.Data.GetProperty("revision").GetInt32();

        var update = await InvokeAsync("tags.update", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            tagId,
            category = "gameplay",
            sortOrder = 3,
            starred = true,
            displayName = "玩法·标签",
            expectedRevision = revision,
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal("gameplay", update.Data.GetProperty("category").GetString());
        Assert.Equal(3, update.Data.GetProperty("sortOrder").GetInt32());
        Assert.True(update.Data.GetProperty("starred").GetBoolean());
        Assert.Equal("玩法·标签", update.Data.GetProperty("displayName").GetString());
        var newRevision = update.Data.GetProperty("revision").GetInt32();
        Assert.True(newRevision > revision);

        // displayName 传 null 清除（回落 name）。
        var clear = await InvokeAsync("tags.update", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            tagId,
            displayName = (string?)null,
            expectedRevision = newRevision,
        });
        Assert.True(clear.Ok, clear.Error?.Message);
        Assert.Equal(JsonValueKind.Null, clear.Data.GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task EngineTag_NameImmutable_RenameGoesToDisplayName_OtherFieldsEditable()
    {
        var (gameId, _) = await CreateGameAsync("tags-engine-rename");

        var list = await InvokeAsync("tags.list", new { });
        var engineTag = list.Data.GetProperty("items").EnumerateArray()
            .First(t => t.GetProperty("kind").GetString() == "engine");
        var tagId = engineTag.GetProperty("tagId").GetString()!;
        var revision = engineTag.GetProperty("revision").GetInt32();
        var engineName = engineTag.GetProperty("name").GetString()!;
        // 扫描自动创建的 engine 标签 category 固定 'engine'（迁移 v22 存量回填同规则）。
        Assert.Equal("engine", engineTag.GetProperty("category").GetString());

        // name 是身份键：engine 标签不可改。
        var rename = await InvokeAsync("tags.update", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            tagId,
            name = "改名尝试",
            expectedRevision = revision,
        });
        Assert.False(rename.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, rename.Error!.Code);

        // 用户改名落 displayName；color/category/sortOrder/starred 对 engine 标签同样开放。
        var update = await InvokeAsync("tags.update", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            tagId,
            displayName = "吉里吉里",
            color = "#8BE9FD",
            category = "engine",
            sortOrder = 2,
            starred = true,
            expectedRevision = revision,
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal(engineName, update.Data.GetProperty("name").GetString());
        Assert.Equal("吉里吉里", update.Data.GetProperty("displayName").GetString());
        Assert.Equal("#8BE9FD", update.Data.GetProperty("color").GetString());
        Assert.True(update.Data.GetProperty("starred").GetBoolean());
        var newRevision = update.Data.GetProperty("revision").GetInt32();
        Assert.True(newRevision > revision);

        // 改名不影响扫描识别与 Suppress 机制（覆盖按 (kind, name) 匹配，name 未变）。
        var game = await InvokeAsync("games.get", new { gameId });
        var gameRevision = game.Data.GetProperty("revision").GetInt32();
        var unassign = await InvokeAsync("tags.unassign", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = gameRevision,
        });
        Assert.True(unassign.Ok, unassign.Error?.Message);
        Assert.True(unassign.Data.GetProperty("suppressedAuto").GetBoolean());

        var reset = await InvokeAsync("tags.reset", new
        {
            idempotencyKey = $"tagr-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = gameRevision,
        });
        Assert.True(reset.Ok, reset.Error?.Message);
        Assert.True(reset.Data.GetProperty("restored").GetBoolean());
        var restored = await InvokeAsync("games.get", new { gameId });
        Assert.Contains(restored.Data.GetProperty("tags").EnumerateArray(),
            t => t.GetProperty("name").GetString() == engineName);
    }

    [Fact]
    public async Task Accept_CreatesEngineTag_GameCarriesIt()
    {
        var (gameId, _) = await CreateGameAsync("tags-auto");

        var game = await InvokeAsync("games.get", new { gameId });
        var tags = game.Data.GetProperty("tags");
        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Contains(tags.EnumerateArray(), t => t.GetProperty("kind").GetString() == "engine");

        var list = await InvokeAsync("tags.list", new { });
        Assert.True(list.Data.GetProperty("total").GetInt32() >= 1);
        Assert.Contains(list.Data.GetProperty("items").EnumerateArray(),
            t => t.GetProperty("kind").GetString() == "engine" && t.GetProperty("gameCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task UserTag_CreateAssignUnassign_FullLifecycle()
    {
        var (gameId, revision) = await CreateGameAsync("tags-user");

        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "年度最佳",
            color = "#66C0F4",
        });
        Assert.True(create.Ok, create.Error?.Message);
        var tagId = create.Data.GetProperty("tagId").GetString()!;

        // 同名拒绝。
        var duplicate = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "年度最佳",
        });
        Assert.False(duplicate.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, duplicate.Error!.Code);

        // assign（游戏修订乐观锁）。
        var assign = await InvokeAsync("tags.assign", new
        {
            idempotencyKey = $"taga-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = revision,
        });
        Assert.True(assign.Ok, assign.Error?.Message);

        var game = await InvokeAsync("games.get", new { gameId });
        Assert.Contains(game.Data.GetProperty("tags").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "年度最佳");

        // 标签过滤（数据库侧）。
        var filtered = await InvokeAsync("games.list", new { tagId, limit = 100 });
        Assert.Equal(1, filtered.Data.GetProperty("total").GetInt32());

        // unassign 用户标签：不产生 Suppress。
        var unassign = await InvokeAsync("tags.unassign", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = game.Data.GetProperty("revision").GetInt32(),
        });
        Assert.True(unassign.Ok, unassign.Error?.Message);
        Assert.False(unassign.Data.GetProperty("suppressedAuto").GetBoolean());

        var after = await InvokeAsync("games.get", new { gameId });
        Assert.DoesNotContain(after.Data.GetProperty("tags").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "年度最佳");
    }

    [Fact]
    public async Task UnassignEngineTag_RegistersSuppressed_ResetRestores()
    {
        var (gameId, revision) = await CreateGameAsync("tags-suppress");

        var game = await InvokeAsync("games.get", new { gameId });
        var engineTag = game.Data.GetProperty("tags").EnumerateArray()
            .First(t => t.GetProperty("kind").GetString() == "engine");
        var engineName = engineTag.GetProperty("name").GetString()!;
        var list = await InvokeAsync("tags.list", new { });
        var tagId = list.Data.GetProperty("items").EnumerateArray()
            .First(t => t.GetProperty("kind").GetString() == "engine"
                && t.GetProperty("name").GetString() == engineName)
            .GetProperty("tagId").GetString()!;

        // 解除自动标签 → Suppress 登记。
        var unassign = await InvokeAsync("tags.unassign", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = game.Data.GetProperty("revision").GetInt32(),
        });
        Assert.True(unassign.Ok, unassign.Error?.Message);
        Assert.True(unassign.Data.GetProperty("suppressedAuto").GetBoolean());

        // reset：清除 Suppress 并按引擎恢复。
        var reset = await InvokeAsync("tags.reset", new
        {
            idempotencyKey = $"tagr-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = game.Data.GetProperty("revision").GetInt32(),
        });
        Assert.True(reset.Ok, reset.Error?.Message);
        Assert.True(reset.Data.GetProperty("restored").GetBoolean());

        var restored = await InvokeAsync("games.get", new { gameId });
        Assert.Contains(restored.Data.GetProperty("tags").EnumerateArray(),
            t => t.GetProperty("name").GetString() == engineName);
    }

    [Fact]
    public async Task TagUpdate_And_Remove_ReturnAffectedGames()
    {
        var (gameId, revision) = await CreateGameAsync("tags-rm");

        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "待整理",
        });
        var tagId = create.Data.GetProperty("tagId").GetString()!;
        var tagRevision = create.Data.GetProperty("revision").GetInt32();

        // 改名。
        var update = await InvokeAsync("tags.update", new
        {
            idempotencyKey = $"tagu-{Guid.NewGuid():N}",
            tagId,
            name = "已整理",
            expectedRevision = tagRevision,
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal("已整理", update.Data.GetProperty("name").GetString());
        var newTagRevision = update.Data.GetProperty("revision").GetInt32();
        Assert.True(newTagRevision > tagRevision);

        // assign 后删除：返回受影响游戏列表。
        var assign = await InvokeAsync("tags.assign", new
        {
            idempotencyKey = $"taga-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = revision,
        });
        Assert.True(assign.Ok, assign.Error?.Message);

        // 自动标签不可编辑。
        var engineList = await InvokeAsync("tags.list", new { });
        var engineTag = engineList.Data.GetProperty("items").EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("kind").GetString() == "engine");
        if (engineTag.ValueKind == JsonValueKind.Object)
        {
            var engineUpdate = await InvokeAsync("tags.update", new
            {
                idempotencyKey = $"tagu-{Guid.NewGuid():N}",
                tagId = engineTag.GetProperty("tagId").GetString(),
                name = "改名尝试",
                expectedRevision = engineTag.GetProperty("revision").GetInt32(),
            });
            Assert.False(engineUpdate.Ok);
        }

        var remove = await InvokeAsync("tags.remove", new
        {
            idempotencyKey = $"tagrm-{Guid.NewGuid():N}",
            tagId,
            expectedRevision = newTagRevision,
        });
        Assert.True(remove.Ok, remove.Error?.Message);
        Assert.Contains(remove.Data.GetProperty("affectedGames").EnumerateArray(),
            g => g.GetString() == gameId);

        var list = await InvokeAsync("tags.list", new { });
        Assert.DoesNotContain(list.Data.GetProperty("items").EnumerateArray(),
            t => t.GetProperty("tagId").GetString() == tagId);
    }

    [Fact]
    public async Task TagsList_DoesNotCountRemovedGames()
    {
        var (gameId, revision) = await CreateGameAsync("tags-removed-count");
        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = $"仅供移除计数-{Guid.NewGuid():N}",
        });
        Assert.True(create.Ok, create.Error?.Message);
        var tagId = create.Data.GetProperty("tagId").GetString()!;

        var assign = await InvokeAsync("tags.assign", new
        {
            idempotencyKey = $"taga-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = revision,
        });
        Assert.True(assign.Ok, assign.Error?.Message);

        var before = await InvokeAsync("tags.list", new { });
        var beforeTag = before.Data.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("tagId").GetString() == tagId);
        Assert.Equal(1, beforeTag.GetProperty("gameCount").GetInt32());

        var game = await InvokeAsync("games.get", new { gameId });
        var remove = await InvokeAsync("games.remove", new
        {
            idempotencyKey = $"game-remove-{Guid.NewGuid():N}",
            gameId,
            expectedRevision = game.Data.GetProperty("revision").GetInt32(),
        });
        Assert.True(remove.Ok, remove.Error?.Message);

        var after = await InvokeAsync("tags.list", new { });
        var afterTag = after.Data.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("tagId").GetString() == tagId);
        Assert.Equal(0, afterTag.GetProperty("gameCount").GetInt32());
    }

    [Fact]
    public async Task Assign_WithStaleGameRevision_IsRejected()
    {
        var (gameId, revision) = await CreateGameAsync("tags-stale");
        var create = await InvokeAsync("tags.create", new
        {
            idempotencyKey = $"tagc-{Guid.NewGuid():N}",
            name = "冲突测试",
        });
        var tagId = create.Data.GetProperty("tagId").GetString()!;

        var assign = await InvokeAsync("tags.assign", new
        {
            idempotencyKey = $"taga-{Guid.NewGuid():N}",
            gameId,
            tagId,
            expectedRevision = revision + 100,
        });
        Assert.False(assign.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, assign.Error!.Code);
        Assert.Equal(revision, assign.Error.CurrentRevision);
    }
}
