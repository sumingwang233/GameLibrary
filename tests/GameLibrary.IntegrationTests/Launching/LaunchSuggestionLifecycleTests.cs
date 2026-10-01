using System.ComponentModel;
using System.Diagnostics;
using GameLibrary.Contracts;
using GameLibrary.Domain.Detection;
using GameLibrary.Host.Launching;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Launching;

public sealed class LaunchSuggestionLifecycleTests
{
    private static string Stub => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");
    private static LaunchRegistry Registry() => new() { VerificationDuration = TimeSpan.FromSeconds(1), ObservationTimeout = TimeSpan.FromSeconds(4) };
    private static LaunchProfile Suggest(LaunchRegistry registry)
    {
        registry.AddSuggestions("game", AppContext.BaseDirectory, [new EntryCandidate(Path.GetFileName(Stub), 80, ["single-entry"])]);
        return Assert.Single(registry.ListProfiles("game"));
    }
    private static LaunchAttempt Start(LaunchRegistry registry, LaunchProfile profile, params string[] args)
    {
        // Test-only argv injection retains automatic provenance; production discovery uses empty argv.
        registry.RestoreProfile(profile with { Arguments = args });
        var plan = registry.CreatePlan(profile.GameId, profile.ProfileId);
        return registry.Execute(Guid.NewGuid().ToString("N"), plan.PlanId, null, null, null);
    }
    private static async Task Wait(Func<bool> done, int seconds = 8)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(50);
        Assert.True(done(), "等待测试进程状态超时");
    }

    [Fact]
    public async Task LiveSuggestionPromotesAndKeepsOneAttemptUntilExit()
    {
        var registry = Registry();
        var profile = Suggest(registry);
        Assert.Equal(profile.ProfileId, registry.RecommendedProfileId("game"));
        var releaseFile = Path.Combine(Path.GetTempPath(), "gamelibrary-launch-" + Guid.NewGuid().ToString("N"));
        var attempt = Start(registry, profile, "--wait-for-file", releaseFile);
        try
        {
            // Keep the real process alive until observation succeeds, independent of CI scheduling speed.
            await Wait(() => registry.GetProfile(profile.ProfileId)?.ValidationStatus == "verified", 20);
            Assert.True(registry.GetProfile(profile.ProfileId)!.IsDefault);
            Assert.Equal("processCreated", registry.GetAttempt(attempt.AttemptId)!.State);
        }
        finally
        {
            File.WriteAllText(releaseFile, "exit");
            await Wait(() => registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
            File.Delete(releaseFile);
        }
        Assert.Single(registry.History("game"));
    }

    [Theory]
    [InlineData(0, "inconclusive")]
    [InlineData(1, "inconclusive")]
    [InlineData(-1073741510, "inconclusive")]
    [InlineData(-1073741819, "discarded")]
    public async Task ShortExitOnlyConfirmedCrashDiscards(int exitCode, string status)
    {
        var registry = Registry();
        var profile = Suggest(registry);
        var attempt = Start(registry, profile, "--exit-code", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Wait(() => registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
        Assert.Equal(status, registry.GetProfile(profile.ProfileId)!.ValidationStatus);
        Assert.False(registry.GetProfile(profile.ProfileId)!.IsDefault);
        if (status == "discarded")
        {
            Assert.Null(registry.RecommendedProfileId("game"));
            Assert.Throws<LaunchException>(() => registry.CreatePlan("game", profile.ProfileId));
            registry.AddSuggestions("game", AppContext.BaseDirectory, [new EntryCandidate(Path.GetFileName(Stub), 80, [])]);
            Assert.Equal("discarded", registry.GetProfile(profile.ProfileId)!.ValidationStatus);
            registry.RestoreSuggestion(profile.ProfileId, registry.GetProfile(profile.ProfileId)!.Revision);
            Assert.Equal(profile.ProfileId, registry.RecommendedProfileId("game"));
        }
    }

    [Fact]
    public async Task LauncherChildKeepsAttemptActiveAfterParentExit()
    {
        var registry = Registry();
        var profile = Suggest(registry);
        var attempt = Start(registry, profile, "--spawn-child", "--hold-ms", "1200");
        await Task.Delay(1800);
        Assert.False(registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
        Assert.Throws<LaunchException>(() => registry.Execute("another-key", null, profile.ProfileId, null, null));
        await Wait(() => registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
        Assert.Equal("verified", registry.GetProfile(profile.ProfileId)!.ValidationStatus);
        Assert.InRange((registry.GetAttempt(attempt.AttemptId)!.FinishedUtc - attempt.ProcessStartedUtc)!.Value.TotalSeconds, 2.5, 5);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("default")]
    [InlineData("remove")]
    [InlineData("inactive")]
    [InlineData("clear")]
    public async Task ManualChangesAndLibrarySwitchPreventPromotion(string change)
    {
        var registry = Registry();
        var profile = Suggest(registry);
        var attempt = Start(registry, profile, "--hold-ms", "2000");
        switch (change)
        {
            case "edit": registry.UpdateProfile(profile.ProfileId, Stub, ["--hold-ms", "2000"], AppContext.BaseDirectory); break;
            case "default": registry.AddProfile("game", Stub, [], AppContext.BaseDirectory, isDefault: true); break;
            case "remove": registry.RemoveProfile(profile.ProfileId); break;
            case "inactive": registry.IsActiveGame = _ => false; break;
            case "clear": registry.Clear(); break;
        }
        await Task.Delay(2500);
        Assert.NotEqual("verified", registry.GetProfile(profile.ProfileId)?.ValidationStatus);
        if (change == "default") Assert.Equal("manual", registry.GetDefaultProfile("game")!.Source);
        if (change == "clear") Assert.Null(registry.GetAttempt(attempt.AttemptId));
    }

    [Fact]
    public void DeletedEntryIsSuppressedAndManualCreationConvertsSuggestion()
    {
        var registry = Registry();
        var profile = Suggest(registry);
        registry.RemoveProfile(profile.ProfileId);
        registry.AddSuggestions("game", AppContext.BaseDirectory, [new EntryCandidate(Path.GetFileName(Stub), 100, [])]);
        Assert.Empty(registry.ListProfiles("game"));
        var manual = registry.AddProfile("game", Stub, ["--manual"], AppContext.BaseDirectory, isDefault: true);
        Assert.Equal(profile.ProfileId, manual.ProfileId);
        Assert.Equal("manual", manual.Source);
        Assert.Equal("--manual", Assert.Single(manual.Arguments));
        Assert.Single(registry.ListProfiles("game"));
    }

    [Fact]
    public void ManualConfigurationOfVerifiedEntryPreservesItsExistingDefault()
    {
        var registry = Registry();
        var profile = Suggest(registry);
        registry.RestoreProfile(profile with { ValidationStatus = "verified", IsDefault = true });
        var manual = registry.AddProfile("game", Stub, ["--manual"], AppContext.BaseDirectory);
        Assert.Equal(profile.ProfileId, manual.ProfileId);
        Assert.Equal("manual", manual.Source);
        Assert.True(manual.IsDefault);
        Assert.Equal(manual.ProfileId, registry.GetDefaultProfile("game")!.ProfileId);
    }

    [Fact]
    public void EnvironmentalErrorsAndUserCancellationAreNeverBadExecutables()
    {
        foreach (var code in new[] { 2, 3, 5, 53, 740, 1223 }) Assert.False(LaunchRegistry.IsBadExecutable(new Win32Exception(code)));
        Assert.True(LaunchRegistry.IsBadExecutable(new Win32Exception(193)));
        Assert.False(LaunchRegistry.IsCrash(unchecked((int)0xC0000135))); // missing runtime dependency
        Assert.Equal(TimeSpan.FromSeconds(30), new LaunchRegistry().VerificationDuration);
        Assert.Equal(TimeSpan.FromSeconds(120), new LaunchRegistry().ObservationTimeout);
    }

    [Fact]
    public void ReusedParentPidCannotAdoptChildrenBornAfterOriginalParentExit()
    {
        var started = DateTime.UtcNow;
        var exited = started.AddSeconds(1);
        Assert.True(GameProcessTree.StartedDuringParentLifetime(started.AddMilliseconds(500), started, exited));
        Assert.False(GameProcessTree.StartedDuringParentLifetime(started.AddSeconds(2), started, exited));
        Assert.False(GameProcessTree.StartedDuringParentLifetime(started.AddMilliseconds(-1), started, null));
    }

    [Fact]
    public async Task Migration25PreservesManualDefaultAndPersistsSuppression()
    {
        var directory = Path.Combine("D:/Official/GameLibrary/artifacts/test-runs", "suggest-migration-" + Guid.NewGuid().ToString("N"));
        var oldOptions = new SqliteLibraryStoreOptions { AppVersion = "1.6.0", ApiVersion = "1", Migrations = DatabaseMigrations.All.Take(24).ToArray() };
        var initial = await SqliteLibraryStore.InitializeAsync(directory, oldOptions, CancellationToken.None);
        await using (var old = initial.Store!)
        {
            old.InsertGame(new GameCard { GameId = "game", Title = "Test", RootPath = AppContext.BaseDirectory, Kind = "folderGame", Membership = "active", AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            // Old schema does not have metadata columns; insert the historical shape directly.
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "library.db")};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO launch_profiles VALUES ('profile','game','game.exe','[]','cwd',NULL,1,7,$now,$now)";
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var opened = await SqliteLibraryStore.TryOpenAsync(directory, new SqliteLibraryStoreOptions { AppVersion = "1.6.1", ApiVersion = "1" }, CancellationToken.None);
        await using var store = opened.Store!;
        var persisted = Assert.Single(store.ReadProfiles());
        Assert.Equal("manual", persisted.Source);
        Assert.True(persisted.IsDefault);
        Assert.Equal(7, persisted.Revision);
        store.UpsertProfile(persisted with { IsDefault = false, Source = "automatic", ValidationStatus = "deleted", SuggestionScore = 80, SuggestionReasons = ["single-entry"] }, DateTime.UtcNow);
        var roundtrip = Assert.Single(store.ReadProfiles());
        Assert.Equal("deleted", roundtrip.ValidationStatus);
        Assert.Equal("single-entry", Assert.Single(roundtrip.SuggestionReasons!));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), $"pre-migration-v24-to-v{DatabaseMigrations.All.Max(migration => migration.Version)}-*.db"));
        store.UpsertProfile(roundtrip with { ValidationStatus = "suggested", Revision = 8 }, DateTime.UtcNow);
        Assert.False(store.CommitSuggestedProfile("profile", 7, "verified", true, DateTime.UtcNow));
        Assert.True(store.CommitSuggestedProfile("profile", 8, "verified", true, DateTime.UtcNow));
        Assert.True(Assert.Single(store.ReadProfiles()).IsDefault);
    }

    [Fact]
    public async Task ProductionThirtySecondBoundaryAndIdempotency()
    {
        var registry = new LaunchRegistry();
        var profile = Suggest(registry);
        var attempt = Start(registry, profile, "--hold-ms", "31500");
        var replay = registry.Execute(attempt.IdempotencyKey, attempt.PlanId, null, null, null);
        Assert.Equal(attempt.AttemptId, replay.AttemptId);
        await Task.Delay(TimeSpan.FromSeconds(29));
        Assert.False(registry.GetProfile(profile.ProfileId)!.IsDefault);
        await Wait(() => registry.GetProfile(profile.ProfileId)?.ValidationStatus == "verified");
        await Wait(() => registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
        Assert.Single(registry.History());
    }

    [Fact]
    public void InvalidExecutableDiscardsWithoutExecutingAnything()
    {
        var directory = Path.Combine("D:/Official/GameLibrary/artifacts/test-runs", "bad-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "bad.exe"), "not an executable");
        var registry = Registry();
        registry.AddSuggestions("game", directory, [new EntryCandidate("bad.exe", 80, [])]);
        var profile = Assert.Single(registry.ListProfiles());
        var plan = registry.CreatePlan("game", profile.ProfileId);
        Assert.Throws<LaunchException>(() => registry.Execute("bad", plan.PlanId, null, null, null));
        Assert.Equal("discarded", registry.GetProfile(profile.ProfileId)!.ValidationStatus);
    }

    [Fact]
    public async Task ObservationTimeoutKeepsEntryInconclusiveAndStillTracksExit()
    {
        var registry = Registry();
        registry.VerificationDuration = TimeSpan.FromSeconds(10);
        registry.ObservationTimeout = TimeSpan.FromMilliseconds(500);
        var profile = Suggest(registry);
        var attempt = Start(registry, profile, "--hold-ms", "2000");
        await Wait(() => registry.GetProfile(profile.ProfileId)?.ValidationStatus == "inconclusive");
        Assert.False(registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
        await Wait(() => registry.GetAttempt(attempt.AttemptId)!.IsTerminal);
        Assert.False(registry.GetProfile(profile.ProfileId)!.IsDefault);
    }
}
