using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.Host.Hosting;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Translation;

public sealed class TitleTranslationTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        private readonly List<TestTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state, dueTime);
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan time)
        {
            _now += time;
            TestTimer[] due;
            lock (_timers) due = _timers.Where(timer => timer.Due <= _now).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        private sealed class TestTimer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = clock.GetUtcNow() + due;
            public void Fire() { Dispose(); callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = clock.GetUtcNow() + dueTime; return true; }
            public void Dispose() { lock (clock._timers) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static HttpResponseMessage TitleResponse(HttpRequestMessage request, string original, string translated)
    {
        object response = request.Method == HttpMethod.Post
            ? new[] { new { translations = new[] { new { text = translated, to = "zh-Hans" } } } }
            : new object[] { new object[] { new[] { translated, original } } };
        return Json(JsonSerializer.Serialize(response));
    }
    private const string Bing = """[{"translations":[{"text":"夏日回忆","to":"zh-Hans"}]}]""";
    private static GameCard Game(string id, string title = "Summer memories") => new()
    {
        GameId = id,
        Title = title,
        RootPath = $"C:/title-test/{id}",
        Kind = "folderGame",
        Membership = "active",
        AcceptedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };
    private static string DataDir() => Path.Combine("D:/Official/GameLibrary/artifacts/test-runs", "titles-" + Guid.NewGuid().ToString("N"));
    private static SqliteLibraryStoreOptions Options => new() { AppVersion = "1.7.1-test", ApiVersion = "1" };

    [Fact]
    public async Task Providers_ParseSegments_Autodetect_AndSendOnlyTitle()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("[\"Summer memories\"]", await request.Content!.ReadAsStringAsync());
                Assert.DoesNotContain("from=", request.RequestUri.Query);
                return Json(Bing);
            }
            Assert.Contains("sl=auto", request.RequestUri.Query);
            Assert.Contains("tl=zh-CN", request.RequestUri.Query);
            return Json("""[[["夏日","Summer"],["回忆","memories"]],null,"en"]""");
        }));
        var client = new TitleTranslationClient(http);
        var google = await client.TranslateAsync("Summer memories", "balanced", CancellationToken.None);
        var bing = await client.TranslateAsync("Summer memories", "balanced", CancellationToken.None);
        Assert.Equal("夏日回忆", google.Title);
        Assert.Equal("google", google.Provider);
        Assert.Equal("bing", bing.Provider);
        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, uri => uri.Contains("title-test"));
    }

    [Theory]
    [InlineData("カノジョの性癖 -盗聴×妄想-", "女友的癖好——偷听×妄想——", "女友的癖好：偷听×妄想")]
    [InlineData("カノジョの性癖 -盗聴×妄想-", "女友的癖好 —— 偷听×妄想 ——", "女友的癖好：偷听×妄想")]
    [InlineData("Title -Subtitle-", "主标题———副标题———", "主标题：副标题")]
    [InlineData("-Subtitle-", "——副标题——", "副标题")]
    [InlineData("Title -Subtitle", "主标题——副标题", "主标题：副标题")]
    [InlineData("Title -Subtitle-", "主标题—副标题—", "主标题—副标题—")]
    [InlineData("Title -Subtitle-", "主标题：副标题", "主标题：副标题")]
    [InlineData("Title — Subtitle", "主标题——副标题——", "主标题——副标题——")]
    [InlineData("-Subtitle-", "—— ——", null)]
    public async Task Providers_NormalizeHyphenSubtitleDashes_BeforeReturning(string original, string translated, string? expected)
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(TitleResponse(request, original, translated))));
        var client = new TitleTranslationClient(http);
        var result = await client.TranslateAsync(original, "google", CancellationToken.None);
        Assert.Equal(expected, result.Title);
        if (expected is null)
        {
            Assert.Equal("invalidResponse", result.Error);
            return;
        }
        Assert.Equal(expected, (await client.TranslateAsync(original, "bing", CancellationToken.None)).Title);
    }

    [Fact]
    public async Task Job_NormalizesSingleAndBatchTitles_PreservesOriginalAndManualAlias()
    {
        const string original = "カノジョの性癖 -盗聴×妄想-";
        const string translated = "女友的癖好——偷听×妄想——";
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(TitleResponse(request, original, translated))));
        await using var fixture = new PipeServerFixture(new TitleTranslationClient(http));
        var store = fixture.State.Library.Store!;
        store.InsertGame(Game("a", original)); store.InsertGame(Game("b", original));
        var dispatcher = new OperationDispatcher(fixture.State);
        Assert.True(Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a" }, idempotencyKey = "dash-single" }).Ok);
        await fixture.State.Jobs.WaitForIdleAsync();
        Assert.True(Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a", "b" }, idempotencyKey = "dash-batch" }).Ok);
        await fixture.State.Jobs.WaitForIdleAsync();
        foreach (var id in new[] { "a", "b" })
        {
            var game = Data(Invoke(dispatcher, "games.get", new { gameId = id }));
            Assert.Equal(original, game.GetProperty("originalTitle").GetString());
            Assert.Equal("女友的癖好：偷听×妄想", game.GetProperty("title").GetString());
        }
        Assert.True(Invoke(dispatcher, "titles.set_translated", new
        {
            gameId = "a",
            title = translated,
            expectedRevision = 2,
            idempotencyKey = "dash-manual",
        }).Ok);
        Assert.Equal(translated, store.ReadTitleTranslation("a")!.TranslatedTitle);
    }

    [Fact]
    public async Task RateLimit_FallsBack_RespectsRetryAfter_AndSkipsCoolingProvider()
    {
        var clock = new Clock();
        var google = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Json(Bing));
            google++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        }));
        var client = new TitleTranslationClient(http, clock);
        Assert.Equal("bing", (await client.TranslateAsync("Summer memories", "google", CancellationToken.None)).Provider);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("bing", (await client.TranslateAsync("Summer memories", "google", CancellationToken.None)).Provider);
        Assert.Equal(1, google);
        clock.Advance(TimeSpan.FromSeconds(61));
        await client.TranslateAsync("Summer memories", "google", CancellationToken.None);
        Assert.Equal(2, google);
    }

    [Fact]
    public async Task InvalidResponses_CoolDownBoth_WithoutRetryLoop()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        var clock = new Clock();
        var client = new TitleTranslationClient(http, clock);
        Assert.Equal("invalidResponse", (await client.TranslateAsync("Summer memories", "bing", CancellationToken.None)).Error);
        Assert.True(client.Unavailable);
        await client.TranslateAsync("Summer memories", "balanced", CancellationToken.None);
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(client.Unavailable);
        Assert.Equal("invalidTitle", (await client.TranslateAsync(new string('a', 1001), "bing", CancellationToken.None)).Error);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Requests_HavePerProviderSpacing_AndCancelInFlight()
    {
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Json(Bing);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json(Bing);
        }));
        var client = new TitleTranslationClient(http, clock);
        await client.TranslateAsync("Summer memories", "bing", CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var second = client.TranslateAsync("Summer memories", "bing", cancel.Token);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(client.Unavailable);
    }

    [Fact]
    public async Task Timeout_CoolsProvider_AndFailsOver()
    {
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post) return Json(Bing);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json("[]");
        }));
        var client = new TitleTranslationClient(http, clock);
        var result = client.TranslateAsync("Summer memories", "google", CancellationToken.None);
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal("bing", (await result.WaitAsync(TimeSpan.FromSeconds(2))).Provider);
    }

    [Fact]
    public async Task Store_PreservesOriginal_SearchesBoth_TogglesOffline_AndRejectsStaleEdits()
    {
        var data = DataDir();
        var init = await SqliteLibraryStore.InitializeAsync(data, Options, CancellationToken.None);
        await using (var store = init.Store!)
        {
            store.InsertGame(Game("a")); store.InsertGame(Game("b", "Another title"));
            Assert.Equal(2, store.SetGameField("a", "title", "My original", "user", 1, DateTime.UtcNow));
            Assert.Equal(3, store.SaveTitleTranslation("a", "My original", "夏日回忆", "bing", 2, DateTime.UtcNow));
            Assert.Equal("My original", store.TryGetGame("a")!.Title);
            Assert.Equal("translated", store.ReadTitleTranslation("a")!.DisplayMode);
            Assert.Null(store.EditTitleTranslation("a", "stale", null, 2, DateTime.UtcNow));
            Assert.Equal("夏日回忆", store.ReadTitleTranslation("a")!.TranslatedTitle);
            Assert.Equal(1, store.QueryGames("My original", false, null, "title", 1, 0).Total);
            Assert.Equal(1, store.QueryGames("夏日", false, null, "title", 1, 0).Total);
            Assert.Single(store.QueryGames(null, false, null, "title", 1, 1).Items);
            store.WriteAutoField("b", "title", "Refreshed original", DateTime.UtcNow);
            Assert.Equal("b", Assert.Single(store.QueryGames("Refreshed original", false, null, "title", 1, 0).Items).GameId);
            Assert.Equal("b", Assert.Single(store.QueryGames(null, false, null, "title", 1, 0).Items).GameId);
            Assert.Equal(4, store.EditTitleTranslation("a", "夏日记忆", null, 3, DateTime.UtcNow));
            Assert.True(store.ReadTitleTranslation("a")!.ManuallyEdited);
            Assert.Equal(5, store.EditTitleTranslation("a", null, "original", 4, DateTime.UtcNow));
            Assert.Equal(6, store.SetGameField("a", "title", "Changed original", "user", 5, DateTime.UtcNow));
            Assert.Null(store.SaveTitleTranslation("a", "My original", "stale", "google", 6, DateTime.UtcNow));
            Assert.Equal("夏日记忆", store.ReadTitleTranslation("a")!.TranslatedTitle);
            Assert.Equal(7, store.EditTitleTranslation("a", null, "translated", 6, DateTime.UtcNow));
            Assert.Equal(8, store.ResetGameField("a", "title", "fallback", 7, DateTime.UtcNow));
            Assert.Equal("original", store.ReadTitleTranslation("a")!.DisplayMode);
            store.RemoveGame("a", 8, new IgnoreRule { IgnoreId = "removed-a", Scope = "ExactPath", GameId = "a", Path = "C:/title-test/a", Reason = "test removal", CreatedUtc = DateTime.UtcNow }, DateTime.UtcNow);
            Assert.Null(store.SaveTitleTranslation("a", "Summer memories", "late", "bing", 9, DateTime.UtcNow));
        }
        var reopened = await SqliteLibraryStore.TryOpenAsync(data, Options, CancellationToken.None);
        await using var saved = reopened.Store!;
        Assert.Equal("夏日记忆", saved.ReadTitleTranslation("a")!.TranslatedTitle);
        Assert.Equal("Summer memories", saved.TryGetGame("a")!.Title);
    }

    [Fact]
    public async Task Upgrade25_CreatesBackup_AndKeepsOriginal()
    {
        var data = DataDir();
        var oldOptions = Options with { Migrations = DatabaseMigrations.All.Take(25).ToArray() };
        var old = await SqliteLibraryStore.InitializeAsync(data, oldOptions, CancellationToken.None);
        old.Store!.InsertGame(Game("a")); await old.Store.DisposeAsync();
        var titleUpgradeOptions = Options with { Migrations = DatabaseMigrations.All.Take(26).ToArray() };
        var upgraded = await SqliteLibraryStore.TryOpenAsync(data, titleUpgradeOptions, CancellationToken.None);
        await using var store = upgraded.Store!;
        Assert.Equal(26, store.Info.SchemaVersion);
        Assert.Equal("Summer memories", store.TryGetGame("a")!.Title);
        Assert.Null(store.ReadTitleTranslation("a"));
        Assert.Single(Directory.GetFiles(Path.Combine(data, "backups"), "pre-migration-v25-to-v26-*.db"));
        var backup = Path.Combine(data, "title-backup.db");
        store.SaveTitleTranslation("a", "Summer memories", "夏日回忆", "bing", 1, DateTime.UtcNow);
        await store.CreateBackupAsync(backup, CancellationToken.None);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT translated_title FROM game_title_translations WHERE game_id = 'a'";
        Assert.Equal("夏日回忆", command.ExecuteScalar());
    }

    private static Envelope<object> Invoke(OperationDispatcher dispatcher, string operation, object parameters) => dispatcher.Dispatch(new IpcRequest
    {
        RequestId = Guid.NewGuid().ToString("N"),
        OperationId = operation,
        ClientName = "title-tests",
        Parameters = JsonSerializer.SerializeToElement(parameters),
    });
    private static JsonElement Data(Envelope<object> result) => JsonSerializer.SerializeToElement(result.Data, ContractJson.Options);

    [Fact]
    public async Task Job_SingleBatchSamePath_SkipsAliases_ForceUsesOriginal_AndReturnsPatches()
    {
        var inputs = new ConcurrentBag<string>();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            inputs.Add(await request.Content!.ReadAsStringAsync()); return Json(Bing);
        }));
        var clock = new Clock();
        await using var fixture = new PipeServerFixture(new TitleTranslationClient(http, clock));
        var store = fixture.State.Library.Store!;
        store.InsertGame(Game("a")); store.InsertGame(Game("same", "夏日回忆"));
        store.WriteSettingsKeys([("titleTranslationEngine", "bing")], DateTime.UtcNow);
        var dispatcher = new OperationDispatcher(fixture.State);
        var accepted = Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a" }, idempotencyKey = "one" });
        Assert.True(accepted.Ok, accepted.Error?.Message);
        var replay = Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a" }, idempotencyKey = "one" });
        Assert.Equal(accepted.JobId, replay.JobId);
        await fixture.State.Jobs.WaitForIdleAsync();
        var progress = Data(Invoke(dispatcher, "jobs.get", new { jobId = accepted.JobId })).GetProperty("result");
        Assert.Equal(1, progress.GetProperty("succeeded").GetInt32());
        Assert.Equal("夏日回忆", progress.GetProperty("items")[0].GetProperty("patch").GetProperty("title").GetString());
        var game = Data(Invoke(dispatcher, "games.get", new { gameId = "a" }));
        Assert.Equal("Summer memories", game.GetProperty("originalTitle").GetString());
        Invoke(dispatcher, "titles.set_display", new { gameId = "a", mode = "original", expectedRevision = 2, idempotencyKey = "display" });
        var skipped = Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a" }, idempotencyKey = "skip" });
        await fixture.State.Jobs.WaitForIdleAsync();
        Assert.Equal("original", store.ReadTitleTranslation("a")!.DisplayMode);
        Assert.Single(inputs);
        clock.Advance(TimeSpan.FromSeconds(2));
        Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a", "same" }, force = true, idempotencyKey = "force" });
        // Second provider request starts after the per-provider interval.
        while (inputs.Count < 2) await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.State.Jobs.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("translated", store.ReadTitleTranslation("a")!.DisplayMode);
        Assert.Contains("[\"Summer memories\"]", inputs);
        Assert.Null(store.ReadTitleTranslation("same"));
    }

    [Fact]
    public async Task Job_LateResponseCannotOverwriteTitleEdit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Json(Bing); }));
        await using var fixture = new PipeServerFixture(new TitleTranslationClient(http));
        var store = fixture.State.Library.Store!;
        store.InsertGame(Game("a"));
        var dispatcher = new OperationDispatcher(fixture.State);
        var accepted = Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a" }, engine = "bing", idempotencyKey = "conflict" });
        await entered.Task;
        store.SetGameField("a", "title", "User edit", "user", 1, DateTime.UtcNow);
        release.TrySetResult();
        await fixture.State.Jobs.WaitForIdleAsync();
        Assert.Null(store.ReadTitleTranslation("a"));
        var progress = Data(Invoke(dispatcher, "jobs.get", new { jobId = accepted.JobId })).GetProperty("result");
        Assert.Equal("revisionConflict", progress.GetProperty("items")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Job_CancelRetainsSuccessAndPending_AndReleasesMaintenanceLease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (request.Method == HttpMethod.Post)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Json(Bing);
            }
            return Json("""[[["夏日回忆","Summer memories"]]]""");
        }));
        await using var fixture = new PipeServerFixture(new TitleTranslationClient(http));
        var store = fixture.State.Library.Store!;
        store.InsertGame(Game("a")); store.InsertGame(Game("b")); store.InsertGame(Game("c"));
        var dispatcher = new OperationDispatcher(fixture.State);
        var accepted = Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a", "b", "c" }, idempotencyKey = "cancel" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (store.ReadTitleTranslation("a") is null)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(10);
        }
        Assert.Null(fixture.State.Sessions.TryBeginMaintenance(() => fixture.State.Jobs.ActiveJobCount() != 0));
        Assert.True(Invoke(dispatcher, "jobs.cancel", new { jobId = accepted.JobId, idempotencyKey = "cancel-request" }).Ok);
        await fixture.State.Jobs.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var job = Data(Invoke(dispatcher, "jobs.get", new { jobId = accepted.JobId }));
        Assert.Equal("cancelled", job.GetProperty("state").GetString());
        Assert.Equal(1, job.GetProperty("result").GetProperty("succeeded").GetInt32());
        Assert.Equal(2, job.GetProperty("result").GetProperty("pending").GetInt32());
        Assert.NotNull(store.ReadTitleTranslation("a"));
        Assert.Null(store.ReadTitleTranslation("b")); Assert.Null(store.ReadTitleTranslation("c"));
        Assert.Equal(2, calls);
        var drained = fixture.State.Sessions.TryBeginMaintenance(() => fixture.State.Jobs.ActiveJobCount() != 0);
        Assert.NotNull(drained); await drained; fixture.State.Sessions.EndMaintenance();
    }

    [Fact]
    public async Task Job_UnavailableStopsQueue_AndRetryOnlyProcessesPendingAfterCooldown()
    {
        var clock = new Clock(); var unhealthy = true; var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(unhealthy ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : request.Method == HttpMethod.Post ? Json(Bing) : Json("""[[["夏日回忆","Summer memories"]]]"""));
        }));
        await using var fixture = new PipeServerFixture(new TitleTranslationClient(http, clock));
        var store = fixture.State.Library.Store!;
        store.InsertGame(Game("a")); store.InsertGame(Game("b")); store.InsertGame(Game("c"));
        var dispatcher = new OperationDispatcher(fixture.State);
        var accepted = Invoke(dispatcher, "titles.translate", new { gameIds = new[] { "a", "b", "c" }, idempotencyKey = "fail" });
        await fixture.State.Jobs.WaitForIdleAsync();
        var job = Data(Invoke(dispatcher, "jobs.get", new { jobId = accepted.JobId }));
        Assert.Equal("failed", job.GetProperty("state").GetString());
        var result = job.GetProperty("result");
        Assert.True(result.GetProperty("pending").GetInt32() >= 1);
        Assert.Equal("enginesUnavailable", result.GetProperty("stopReason").GetString());
        Assert.Equal(2, calls);
        unhealthy = false; clock.Advance(TimeSpan.FromSeconds(61));
        var pending = result.GetProperty("unprocessedGameIds").EnumerateArray().Select(id => id.GetString()!).Take(1).ToArray();
        Invoke(dispatcher, "titles.translate", new { gameIds = pending, idempotencyKey = "retry-pending" });
        await fixture.State.Jobs.WaitForIdleAsync();
        Assert.NotNull(store.ReadTitleTranslation(pending[0]));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ProviderConcurrency_IsAtMostOnePerEngineAndTwoGlobally()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var peak = 0;
        var perEngine = new ConcurrentDictionary<string, int>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var host = request.RequestUri!.Host;
            Assert.Equal(1, perEngine.AddOrUpdate(host, 1, (_, value) => value + 1));
            var count = Interlocked.Increment(ref active);
            if (count == 2) Interlocked.Exchange(ref peak, 2);
            else Interlocked.CompareExchange(ref peak, 1, 0);
            if (count == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active); perEngine[host]--;
            return request.Method == HttpMethod.Post ? Json(Bing) : Json("""[[["夏日回忆","Summer memories"]]]""");
        }));
        var client = new TitleTranslationClient(http);
        var tasks = Enumerable.Range(0, 4).Select(_ => client.TranslateAsync("Summer memories", "balanced", CancellationToken.None)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, active);
        release.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, peak);
    }
}
