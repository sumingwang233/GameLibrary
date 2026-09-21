using System.Security.Cryptography;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Review;

/// <summary>
/// T11 入库/忽略（经真实管道）：手动扫描直接进入 pendingReview → accept/defer/ignore 状态机、
/// accept 幂等返回 GameId、Revision 冲突、忽略规则抑制与撤销恢复。
/// </summary>
public sealed class CandidateReviewTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public CandidateReviewTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static string StubExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    [Fact]
    public void ElevationFallback_PreservesArguments_AndReportsCancellation()
    {
        var original = new System.Diagnostics.ProcessStartInfo { FileName = @"F:\日本語\Game.exe", WorkingDirectory = @"F:\日本語", UseShellExecute = false };
        original.ArgumentList.Add("argument with spaces");
        var calls = 0;
        var error = Assert.Throws<GameLibrary.Host.Launching.LaunchException>(() =>
            GameLibrary.Host.Launching.LaunchRegistry.StartProcessWithElevationFallback(original, info =>
            {
                if (++calls == 1) throw new System.ComponentModel.Win32Exception(740);
                Assert.True(info.UseShellExecute);
                Assert.Equal("runas", info.Verb);
                Assert.Equal(original.FileName, info.FileName);
                Assert.Equal(original.WorkingDirectory, info.WorkingDirectory);
                Assert.Equal(original.ArgumentList, info.ArgumentList);
                throw new System.ComponentModel.Win32Exception(1223);
            }));
        Assert.Equal(2, calls);
        Assert.Contains("取消", error.Message);
    }

    [Fact]
    public async Task DeleteFiles_RecyclesOnlyConfirmedGame_AndReplayIsSafe()
    {
        var root = CreateGameTree("recycle-game");
        var target = Path.Combine(root, "GameA");
        var sibling = Path.Combine(root, "keep.txt");
        File.WriteAllText(sibling, "keep");
        await InvokeAsync("roots.add", new { root });
        var created = await InvokeAsync("games.create", new { sourcePath = target, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(created.Ok, created.Error?.Message);
        var gameId = created.Data.GetProperty("gameId").GetString()!;
        var parameters = new { gameId, expectedRevision = 1, deleteFiles = true, confirmedPath = target, idempotencyKey = Guid.NewGuid().ToString() };
        var removed = await InvokeAsync("games.remove", parameters);
        Assert.True(removed.Ok, removed.Error?.Message);
        Assert.True(removed.Data.GetProperty("recycled").GetBoolean());
        Assert.False(Directory.Exists(target));
        Assert.Equal("keep", File.ReadAllText(sibling));
        Assert.True((await InvokeAsync("games.remove", parameters)).Ok);
    }

    [Fact]
    public async Task MovedCandidate_DisappearsAndCanBeRediscovered()
    {
        var root = CreateGameTree("moved-candidate");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);
        var before = await InvokeAsync("candidates.list", new { state = "pendingReview" });
        var oldPath = Path.Combine(root, "GameA");
        var candidate = before.Data.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("physicalPath").GetString() == oldPath);
        var id = candidate.GetProperty("candidateId").GetString()!;
        Directory.Move(oldPath, Path.Combine(root, "Houkago"));
        var after = await InvokeAsync("candidates.list", new { state = "pendingReview" });
        Assert.True(after.Ok, after.Error?.Message);
        Assert.DoesNotContain(after.Data.GetProperty("items").EnumerateArray(), item => item.GetProperty("candidateId").GetString() == id);
        Assert.Equal("observed", _fixture.State.Library.Store!.TryGetCandidate(id)!.ReviewState);
        Directory.Move(Path.Combine(root, "Houkago"), oldPath);
        await ScanAndWaitAsync(root);
        Assert.Equal("pendingReview", _fixture.State.Library.Store.TryGetCandidate(id)!.ReviewState);
    }

    [Fact]
    public async Task DeleteFiles_RequiresConfirmationAndRejectsLibraryRoot()
    {
        var root = CreateGameTree("delete-guard");
        await InvokeAsync("roots.add", new { root });
        var created = await InvokeAsync("games.create", new { sourcePath = root, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(created.Ok, created.Error?.Message);
        var gameId = created.Data.GetProperty("gameId").GetString()!;
        foreach (var confirmedPath in new[] { "", root })
        {
            var result = await InvokeAsync("games.remove", new { gameId, expectedRevision = 1, deleteFiles = true, confirmedPath, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.False(result.Ok);
            Assert.True(File.Exists(Path.Combine(root, "GameA", "Game.exe")));
            Assert.Equal("active", _fixture.State.Library.Store!.TryGetGame(gameId)!.Membership);
        }
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "review-test",
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

    /// <summary>
    /// 指纹用例树（修订门控下的充实形态）：Game.exe + data.xp3 + 2 个小内容文件，
    /// 覆盖 Kirikiri 小文件充实路径；marker 决定字节内容——同 marker 即改名拷贝，异 marker 即无关游戏
    ///（负例必须用字节内容真不同的文件，避免两个 'x' 文件人为相同）。
    /// </summary>
    private static string CreateKirikiriTree(string prefix, string gameName, string marker)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        var gameDir = Path.Combine(path, gameName);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "Game.exe"), $"{marker}-entry");
        File.WriteAllText(Path.Combine(gameDir, "data.xp3"), $"{marker}-xp3");
        File.WriteAllText(Path.Combine(gameDir, "readme.txt"), $"{marker}-readme");
        File.WriteAllText(Path.Combine(gameDir, "config.ini"), $"{marker}-config");
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

    [Fact]
    public async Task ManualScan_ProducesPendingReview_AcceptCreatesGameWithAbsoluteEntry()
    {
        var root = CreateGameTree("review-accept");
        await InvokeAsync("roots.add", new { root });

        await ScanAndWaitAsync(root);

        // 用户主动发起的完整扫描已经是稳定观察，单轮即可进入待审核。
        var list = await InvokeAsync("candidates.list", new { });
        var pending = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("relativePath").GetString() == "GameA"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        Assert.Equal("pendingReview", pending.GetProperty("reviewState").GetString());
        var candidateId = pending.GetProperty("candidateId").GetString()!;
        var pendingRevision = pending.GetProperty("revision").GetInt32();

        // Revision 冲突。
        var stale = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "stale-accept",
            candidateId,
            expectedRevision = pendingRevision - 1,
        });
        Assert.False(stale.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, stale.Error!.Code);

        // R43 原子性：conflict 不落任何写——旧实现先建卡再冲突，会留下孤儿游戏卡。
        var gamesAfterStale = await InvokeAsync("games.list", new { });
        Assert.DoesNotContain(gamesAfterStale.Data.GetProperty("items").EnumerateArray(),
            g => g.GetProperty("rootPath").GetString()!.StartsWith(root, StringComparison.Ordinal));

        // 接受入库。
        var accept = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-" + candidateId,
            candidateId,
            expectedRevision = pendingRevision,
        });
        Assert.True(accept.Ok, accept.Error?.Message);
        var gameId = accept.Data.GetProperty("gameId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(gameId));

        // 同键重试：收据重放返回同一 GameId。
        var replay = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-" + candidateId,
            candidateId,
            expectedRevision = pendingRevision,
        });
        Assert.True(replay.Ok);
        Assert.Equal(gameId, replay.Data.GetProperty("gameId").GetString());

        // 不同键重放已接受候选：原子路径幂等返回同一 GameId，不重复建卡。
        var otherKeyReplay = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-alt-" + candidateId,
            candidateId,
            expectedRevision = pendingRevision,
        });
        Assert.True(otherKeyReplay.Ok, otherKeyReplay.Error?.Message);
        Assert.Equal(gameId, otherKeyReplay.Data.GetProperty("gameId").GetString());

        var acceptedCandidates = await InvokeAsync("candidates.list", new { state = "accepted" });
        Assert.True(acceptedCandidates.Ok, acceptedCandidates.Error?.Message);
        Assert.All(acceptedCandidates.Data.GetProperty("items").EnumerateArray(),
            candidate => Assert.Equal("accepted", candidate.GetProperty("reviewState").GetString()));
        Assert.Contains(acceptedCandidates.Data.GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("candidateId").GetString() == candidateId);

        var pendingCandidates = await InvokeAsync("candidates.list", new { state = "pendingReview" });
        Assert.True(pendingCandidates.Ok, pendingCandidates.Error?.Message);
        Assert.DoesNotContain(pendingCandidates.Data.GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("candidateId").GetString() == candidateId);

        var games = await InvokeAsync("games.list", new { });
        Assert.Contains(games.Data.GetProperty("items").EnumerateArray(),
            g => g.GetProperty("gameId").GetString() == gameId);
        var game = await InvokeAsync("games.get", new { gameId });
        Assert.True(game.Ok);
        Assert.Equal("GameA", game.Data.GetProperty("title").GetString());
        Assert.Equal("kirikiri", game.Data.GetProperty("engine").GetString());
        Assert.Equal(Path.Combine(root, "GameA", "Game.exe"), game.Data.GetProperty("entryPath").GetString());

        // 引擎自动标签随 accept 原子落库（R43：随单事务提交）。
        Assert.Contains(game.Data.GetProperty("tags").EnumerateArray(),
            t => t.GetProperty("kind").GetString() == "engine"
                && t.GetProperty("name").GetString() == "kirikiri");
    }

    [Fact]
    public async Task DeferFlow_MovesToDeferred()
    {
        var root = CreateGameTree("review-defer");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);

        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("reviewState").GetString() == "pendingReview"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var candidateId = item.GetProperty("candidateId").GetString()!;
        var revision = item.GetProperty("revision").GetInt32();

        var defer = await InvokeAsync("candidates.defer", new
        {
            idempotencyKey = "defer-" + candidateId,
            candidateId,
            expectedRevision = revision,
        });
        Assert.True(defer.Ok, defer.Error?.Message);
        Assert.Equal("deferred", defer.Data.GetProperty("reviewState").GetString());

        // deferred 不能直接接受（状态机：Deferred → PendingReview）。
        var illegal = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "deferred-accept-" + candidateId,
            candidateId,
            expectedRevision = defer.Data.GetProperty("revision").GetInt32(),
        });
        Assert.False(illegal.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, illegal.Error!.Code);
    }

    [Fact]
    public async Task IgnoreFlow_ExactPathRule_SuppressesRescan_AndRemoveRestores()
    {
        var root = CreateGameTree("review-ignore");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);

        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("reviewState").GetString() == "pendingReview"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var candidateId = item.GetProperty("candidateId").GetString()!;
        var detail = await InvokeAsync("candidates.get", new { candidateId });
        var physicalPath = detail.Data.GetProperty("physicalPath").GetString()!;
        var revision = item.GetProperty("revision").GetInt32();

        // R48 原子性：Revision 冲突不落任何写——旧实现会先插入忽略规则再冲突，留下孤儿规则。
        var rulesBeforeStale = await InvokeAsync("ignores.list", new { });
        var rulesBeforeStaleTotal = rulesBeforeStale.Data.GetProperty("total").GetInt32();
        var staleIgnore = await InvokeAsync("candidates.ignore", new
        {
            idempotencyKey = "stale-ignore-" + candidateId,
            candidateId,
            expectedRevision = revision - 1,
        });
        Assert.False(staleIgnore.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, staleIgnore.Error!.Code);
        var rulesAfterStale = await InvokeAsync("ignores.list", new { });
        Assert.Equal(rulesBeforeStaleTotal, rulesAfterStale.Data.GetProperty("total").GetInt32());

        var ignore = await InvokeAsync("candidates.ignore", new
        {
            idempotencyKey = "ignore-" + candidateId,
            candidateId,
            expectedRevision = revision,
        });
        Assert.True(ignore.Ok, ignore.Error?.Message);
        var ignoreId = ignore.Data.GetProperty("ignoreId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(ignoreId));

        var rules = await InvokeAsync("ignores.list", new { });
        Assert.Contains(rules.Data.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("ignoreId").GetString() == ignoreId
                && r.GetProperty("scope").GetString() == "ExactPath");

        // 重扫：被 ExactPath 规则抑制，不再出现待审核候选。
        await ScanAndWaitAsync(root);
        var afterRescan = await InvokeAsync("candidates.list", new { });
        Assert.DoesNotContain(afterRescan.Data.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("relativePath").GetString() == "GameA"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal)
                && i.GetProperty("reviewState").GetString() == "pendingReview");

        // 撤销规则：ignored 候选回到 observed（恢复提示的唯一途径）。
        var remove = await InvokeAsync("ignores.remove", new
        {
            idempotencyKey = "unignore-" + ignoreId,
            ignoreId,
        });
        Assert.True(remove.Ok, remove.Error?.Message);
        Assert.Equal(1, remove.Data.GetProperty("restoredCandidates").GetInt32());

        var restored = await InvokeAsync("candidates.get", new { candidateId });
        Assert.Equal("observed", restored.Data.GetProperty("reviewState").GetString());
    }

    [Fact]
    public async Task IgnoreSubtree_ImmediatelySuppressesExistingCandidates()
    {
        var root = CreateGameTree("review-subtree");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);
        var before = await InvokeAsync("candidates.list", new { });
        Assert.True(before.Data.GetProperty("total").GetInt32() >= 1);

        var create = await InvokeAsync("ignores.create", new
        {
            idempotencyKey = "subtree-" + root,
            scope = "Subtree",
            path = root,
            reason = "测试子树忽略",
        });
        Assert.True(create.Ok, create.Error?.Message);
        Assert.Equal(1, create.Data.GetProperty("suppressedCandidates").GetInt32());

        var after = await InvokeAsync("candidates.list", new { });
        Assert.DoesNotContain(after.Data.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal)
                && i.GetProperty("reviewState").GetString() is "observed" or "pendingReview");
    }

    [Fact]
    public async Task IgnoreCreate_OutsideRegisteredRoots_IsDenied()
    {
        var denied = await InvokeAsync("ignores.create", new
        {
            idempotencyKey = "deny-" + Guid.NewGuid().ToString("N"),
            scope = "ExactPath",
            path = @"D:\Official\GameLibrary\artifacts",
        });
        Assert.False(denied.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, denied.Error!.Code);
    }

    /// <summary>取 root 下唯一 pendingReview 候选并 accept；返回 (gameId, accept 响应)。</summary>
    private async Task<(string GameId, Envelope<JsonElement> Accept)> AcceptPendingUnderRoot(string root)
    {
        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("reviewState").GetString() == "pendingReview"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var candidateId = item.GetProperty("candidateId").GetString()!;
        var revision = item.GetProperty("revision").GetInt32();

        var accept = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-" + candidateId,
            candidateId,
            expectedRevision = revision,
        });
        Assert.True(accept.Ok, accept.Error?.Message);
        return (accept.Data.GetProperty("gameId").GetString()!, accept);
    }

    [Fact]
    public async Task AcceptRenamedCopy_RespondsAndPublishesSimilarTo_ExcludingSelf()
    {
        // 同 marker = 两份改名拷贝（字节内容完全相同，仅目录名不同）。
        var root1 = CreateKirikiriTree("fp-sim", "TwinGame", "twin-marker");
        var root2 = CreateKirikiriTree("fp-sim", "TwinGameRenamed", "twin-marker");
        await InvokeAsync("roots.add", new { root = root1 });
        await InvokeAsync("roots.add", new { root = root2 });
        await ScanAndWaitAsync(root1);
        await ScanAndWaitAsync(root2);

        var (firstId, firstAccept) = await AcceptPendingUnderRoot(root1);
        // 第一个 accept：库内无同指纹游戏，similarTo 为空数组（字段形状稳定）。
        Assert.True(firstAccept.Data.GetProperty("similarTo").GetArrayLength() == 0);

        var fingerprintRowsBefore = _fixture.State.Library.Store!
            .ListActiveGameFingerprints(null, GameLibrary.Domain.Identity.FingerprintPolicy.StrategyVersion).Count;

        var (secondId, secondAccept) = await AcceptPendingUnderRoot(root2);
        var similarTo = secondAccept.Data.GetProperty("similarTo");
        // 门控通过：matched=4（入口+xp3+两个小文件全哈希命中）、similarity=1.0。
        Assert.Equal(1, similarTo.GetArrayLength());
        Assert.Equal(firstId, similarTo[0].GetProperty("gameId").GetString());
        Assert.True(similarTo[0].GetProperty("similarity").GetDouble() >= 0.9,
            $"similarity={similarTo[0].GetProperty("similarity").GetDouble()}");
        Assert.Equal("TwinGame", similarTo[0].GetProperty("title").GetString());
        // 排除自身：similarTo 不含第二个游戏自己的 gameId。
        Assert.DoesNotContain(similarTo.EnumerateArray(),
            s => s.GetProperty("gameId").GetString() == secondId);

        // 幂等重放：同键收据重放（参数须与原请求一致，digest 才匹配）与异键 alreadyAccepted
        // 路径都不再写指纹行。
        var replayList = await InvokeAsync("candidates.list", new { state = "accepted" });
        var secondCandidate = replayList.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("physicalPath").GetString()!.StartsWith(root2, StringComparison.Ordinal));
        var secondCandidateId = secondCandidate.GetProperty("candidateId").GetString()!;
        var replay = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-" + secondCandidateId,
            candidateId = secondCandidateId,
            expectedRevision = secondAccept.Data.GetProperty("revision").GetInt32() - 1,
        });
        Assert.True(replay.Ok, replay.Error?.Message);
        Assert.Equal(secondId, replay.Data.GetProperty("gameId").GetString());
        var otherKeyReplay = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-alt-" + secondCandidateId,
            candidateId = secondCandidateId,
            expectedRevision = secondAccept.Data.GetProperty("revision").GetInt32(),
        });
        Assert.True(otherKeyReplay.Ok, otherKeyReplay.Error?.Message);
        Assert.Equal(secondId, otherKeyReplay.Data.GetProperty("gameId").GetString());
        Assert.Equal(fingerprintRowsBefore + 1, _fixture.State.Library.Store!
            .ListActiveGameFingerprints(null, GameLibrary.Domain.Identity.FingerprintPolicy.StrategyVersion).Count);

        // game.created 事件（events.read）附 similarTo。
        var events = await InvokeAsync("events.read", new { limit = 4096 });
        var created = events.Data.GetProperty("items").EnumerateArray()
            .First(ev => ev.GetProperty("type").GetString() == "game.created"
                && ev.GetProperty("payload").GetProperty("gameId").GetString() == secondId);
        var eventSimilarTo = created.GetProperty("payload").GetProperty("similarTo");
        Assert.Equal(1, eventSimilarTo.GetArrayLength());
        Assert.Equal(firstId, eventSimilarTo[0].GetProperty("gameId").GetString());

        // games.get 详情：fingerprint 摘要非空 + 实时 similarTo（同样排除自身）。
        var detail = await InvokeAsync("games.get", new { gameId = secondId });
        Assert.True(detail.Ok, detail.Error?.Message);
        var fingerprint = detail.Data.GetProperty("fingerprint");
        Assert.Equal(GameLibrary.Domain.Identity.FingerprintPolicy.StrategyVersion,
            fingerprint.GetProperty("strategyVersion").GetInt32());
        Assert.True(fingerprint.GetProperty("entryCount").GetInt32() >= 2);
        Assert.False(string.IsNullOrWhiteSpace(fingerprint.GetProperty("computedUtc").GetString()));
        var detailSimilarTo = detail.Data.GetProperty("similarTo");
        Assert.Equal(1, detailSimilarTo.GetArrayLength());
        Assert.Equal(firstId, detailSimilarTo[0].GetProperty("gameId").GetString());

        // 对称方向：第一个游戏的详情也能看到第二个（matched 语义对称）。
        var firstDetail = await InvokeAsync("games.get", new { gameId = firstId });
        Assert.Contains(firstDetail.Data.GetProperty("similarTo").EnumerateArray(),
            s => s.GetProperty("gameId").GetString() == secondId);
    }

    [Fact]
    public async Task AcceptUnrelatedTree_SimilarToEmpty_NoFingerprintGameIsNull()
    {
        // 字节内容真不同（异 marker），避免人为相同的哈希。
        var root = CreateKirikiriTree("fp-diff", "AlienGame", "alien-marker");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);

        var (gameId, accept) = await AcceptPendingUnderRoot(root);
        // 无关游戏：matched=0，过不了门控 → 空数组。
        Assert.Equal(0, accept.Data.GetProperty("similarTo").GetArrayLength());

        // 直造行（无指纹游戏）：fingerprint=null、similarTo=[]。
        var bareGameId = $"game-bare-{Guid.NewGuid():N}";
        var utcNow = DateTime.UtcNow;
        _fixture.State.Library.Store!.InsertGame(new GameLibrary.Infrastructure.Persistence.GameCard
        {
            GameId = bareGameId,
            Title = "无指纹游戏",
            RootPath = Path.Combine(root, "AlienGame"),
            Kind = "directory",
            Membership = "active",
            AcceptedUtc = utcNow,
            UpdatedUtc = utcNow,
        });
        var bare = await InvokeAsync("games.get", new { gameId = bareGameId });
        Assert.True(bare.Ok);
        Assert.Equal(JsonValueKind.Null, bare.Data.GetProperty("fingerprint").ValueKind);
        Assert.Equal(0, bare.Data.GetProperty("similarTo").GetArrayLength());
    }

    [Fact]
    public async Task AcceptPatchedCopy_SimilarityAboveThreshold_StillSuggested()
    {
        // 同 marker 建两份拷贝后，第二份打补丁（readme.txt 内容变更）：
        // matched=3（入口+xp3+config）、similarity=3/4=0.75 ≥ 0.6 且 matched ≥ 2 → 过门控。
        var root1 = CreateKirikiriTree("fp-patch", "PatchedGame", "patch-marker");
        var root2 = CreateKirikiriTree("fp-patch", "PatchedGameCopy", "patch-marker");
        var patchedReadme = Path.Combine(root2, "PatchedGameCopy", "readme.txt");
        File.WriteAllText(patchedReadme, "patch-marker-readme-CHANGED");
        await InvokeAsync("roots.add", new { root = root1 });
        await InvokeAsync("roots.add", new { root = root2 });
        await ScanAndWaitAsync(root1);
        await ScanAndWaitAsync(root2);

        var (firstId, _) = await AcceptPendingUnderRoot(root1);
        var (secondId, secondAccept) = await AcceptPendingUnderRoot(root2);

        var similarTo = secondAccept.Data.GetProperty("similarTo");
        Assert.Equal(1, similarTo.GetArrayLength());
        Assert.Equal(firstId, similarTo[0].GetProperty("gameId").GetString());
        var similarity = similarTo[0].GetProperty("similarity").GetDouble();
        Assert.True(similarity is >= 0.6 and < 1.0, $"similarity={similarity} 应在 [0.6, 1.0) 区间");
    }

    [Fact]
    public async Task RemovedGame_KeepsFingerprintRow_ButNeverSuggested()
    {
        var marker = "removed-marker-" + Guid.NewGuid().ToString("N")[..8];
        var root1 = CreateKirikiriTree("fp-removed", "GoneGame", marker);
        var root2 = CreateKirikiriTree("fp-removed", "GoneGameCopy", marker);
        await InvokeAsync("roots.add", new { root = root1 });
        await InvokeAsync("roots.add", new { root = root2 });
        await ScanAndWaitAsync(root1);
        await ScanAndWaitAsync(root2);

        var (firstId, _) = await AcceptPendingUnderRoot(root1);
        var detail = await InvokeAsync("games.get", new { gameId = firstId });
        var revision = detail.Data.GetProperty("revision").GetInt32();

        // 软移除：指纹行保留（remove 不触碰 game_fingerprints）。
        var removed = await InvokeAsync("games.remove", new
        {
            idempotencyKey = "fp-remove-" + firstId,
            gameId = firstId,
            expectedRevision = revision,
        });
        Assert.True(removed.Ok, removed.Error?.Message);
        Assert.NotNull(_fixture.State.Library.Store!.TryGetGameFingerprint(firstId));

        // 再 accept 其内容改名拷贝：removed 游戏不产生建议（active 过滤生效）。
        var (secondId, secondAccept) = await AcceptPendingUnderRoot(root2);
        Assert.DoesNotContain(secondAccept.Data.GetProperty("similarTo").EnumerateArray(),
            s => s.GetProperty("gameId").GetString() == firstId);

        // games.get 同口径：removed 游戏不在 similarTo 中。
        var secondDetail = await InvokeAsync("games.get", new { gameId = secondId });
        Assert.DoesNotContain(secondDetail.Data.GetProperty("similarTo").EnumerateArray(),
            s => s.GetProperty("gameId").GetString() == firstId);
    }
}
