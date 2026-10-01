using System.Diagnostics;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Backups;
using GameLibrary.Host;
using GameLibrary.Host.Hosting;
using GameLibrary.Infrastructure.Backups;
using GameLibrary.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLibrary.IntegrationTests.Translation;

public sealed class RestoreSafetyTests
{
    [Theory]
    [InlineData("prepared")]
    [InlineData("database-swapped")]
    [InlineData("assets-swapped")]
    [InlineData("committed")]
    public async Task ProcessTermination_RecoversDatabaseAndAssetsTogether(string phase)
    {
        var data = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", Guid.NewGuid().ToString("N"), "data");
        var options = new SqliteLibraryStoreOptions { AppVersion = "test", ApiVersion = ApiConstants.ApiVersion };
        var initialized = await SqliteLibraryStore.InitializeAsync(data, options, CancellationToken.None);
        var store = initialized.Store!;
        var backup = Path.Combine(data, "backups", "source");
        var assets = Path.Combine(data, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "cover.txt"), "backup");
        await store.CreateBackupAsync(Path.Combine(backup, "library.db"), CancellationToken.None);
        BackupArchive.CopyDirectory(assets, Path.Combine(backup, "assets"));
        BackupArchive.WriteManifest(backup, new BackupManifest
        {
            BackupId = "source",
            LibraryInstanceId = store.Info.LibraryInstanceId,
            SourceDataEpoch = store.Info.DataEpoch,
            AppVersion = "test",
            SchemaVersion = store.Info.SchemaVersion,
            CreatedUtc = DateTime.UtcNow,
            Files = BackupArchive.EnumerateFiles(backup),
        });
        var originalEpoch = await store.RenewDataEpochAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(assets, "cover.txt"), "original");
        await store.DisposeAsync();

        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--restore-crash", data, backup, phase }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(86, process.ExitCode);
        LibraryRestoreStorage.RecoverBeforeOpen(data);
        // Recovery is repeatable if the next startup was interrupted too.
        LibraryRestoreStorage.RecoverBeforeOpen(data);
        var reopened = await SqliteLibraryStore.TryOpenAsync(data, options, CancellationToken.None);
        Assert.True(reopened.IsOpened, reopened.Detail);
        await using var recovered = reopened.Store!;
        Assert.Equal(phase == "committed" ? "backup" : "original", await File.ReadAllTextAsync(Path.Combine(assets, "cover.txt")));
        if (phase == "committed") Assert.NotEqual(originalEpoch, recovered.Info.DataEpoch);
        else Assert.Equal(originalEpoch, recovered.Info.DataEpoch);
    }

    [Fact]
    public async Task WriterHeld_ReadRequestsCompleteUsingTheirOwnConnections()
    {
        await using var fixture = new PipeServerFixture();
        var dispatcher = new OperationDispatcher(fixture.State);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => fixture.State.Library.Store!.WriteExclusive((_, _) =>
        {
            locked.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        }));
        await locked.Task;
        try
        {
            foreach (var operation in new[] { "games.list", "events.read", "host.status", "diagnostics.status" })
            {
                var response = await Task.Run(() => dispatcher.DispatchAsync(new IpcRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    OperationId = operation,
                    Parameters = JsonSerializer.SerializeToElement(new { limit = 10 }),
                })).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(response.Ok, response.Error?.Message);
            }
        }
        finally { release.Set(); await writer; }
    }

    [Fact]
    public async Task MissingSafetyBackup_StartsInRecoveryRequired()
    {
        var data = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", Guid.NewGuid().ToString("N"), "data");
        ControlAreaStore.WriteAtomic(Path.Combine(data, "control", "restore-in-progress.json"), JsonSerializer.Serialize(
            new RestoreJournal($".restore-{Guid.NewGuid():N}", "source", "missing-safety", "key", "digest", false, "prepared")));
        await using var runtime = await HostRuntime.StartAsync(data, NullLoggerFactory.Instance);
        Assert.Equal(LibraryOpenStatus.RecoveryRequired, runtime.Library.Status);
        Assert.Null(runtime.Library.Store);
    }

    [Fact]
    public void CommittedJobRecovery_RemainsAvailableAfterAnotherJournalReplacesIt()
    {
        var data = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", Guid.NewGuid().ToString("N"), "data");
        var jobId = $"job-{Guid.NewGuid():N}";
        ControlAreaStore.WriteAtomic(Path.Combine(data, "control", "restore-jobs", jobId + ".json"), JsonSerializer.Serialize(new
        {
            snapshot = new JobSnapshot { JobId = jobId, Kind = "restore", State = "running", CreatedUtc = DateTime.UtcNow },
            result = (object?)null,
        }, ContractJson.Options));
        var journal = new RestoreJournal($".restore-{Guid.NewGuid():N}", "source", "safety", "key", "digest", false,
            "committed", new RestoreResult("source", "instance", "epoch", "safety"), jobId);
        var journalPath = Path.Combine(data, "control", "restore-in-progress.json");
        ControlAreaStore.WriteAtomic(journalPath, JsonSerializer.Serialize(journal));
        var manager = new JobManager();
        manager.ConfigureRestoreHistory(data);
        ControlAreaStore.WriteAtomic(journalPath, JsonSerializer.Serialize(journal with { Phase = "rolled-back", JobId = "another-job" }));
        var restarted = new JobManager();
        restarted.ConfigureRestoreHistory(data);
        Assert.Equal("succeeded", restarted.Get(jobId)!.State);
        var result = Assert.IsType<JsonElement>(restarted.TryGetProgress(jobId)!.Value.Data);
        Assert.Equal("source", result.GetProperty("data").GetProperty("backupId").GetString());
    }

    [Fact]
    public async Task CorruptBackup_IsRejectedBeforeTheActiveStoreIsDetached()
    {
        await using var fixture = new PipeServerFixture();
        var store = fixture.State.Library.Store!;
        var backup = Path.Combine(fixture.State.DataDirectory, "backups", "corrupt");
        await store.CreateBackupAsync(Path.Combine(backup, "library.db"), CancellationToken.None);
        var manifest = new BackupManifest
        {
            BackupId = "corrupt",
            LibraryInstanceId = store.Info.LibraryInstanceId,
            SourceDataEpoch = store.Info.DataEpoch,
            AppVersion = "test",
            SchemaVersion = store.Info.SchemaVersion,
            CreatedUtc = DateTime.UtcNow,
            Files = BackupArchive.EnumerateFiles(backup),
        };
        await File.AppendAllTextAsync(Path.Combine(backup, "library.db"), "corrupted");
        var storage = new LibraryRestoreStorage(fixture.State.DataDirectory, backup, manifest, store,
            new SqliteLibraryStoreOptions { AppVersion = "test", ApiVersion = ApiConstants.ApiVersion },
            "key", "digest", () => throw new Xunit.Sdk.XunitException("corrupt backup detached the active store"), _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => GameLibrary.Application.Backups.BackupRestoreService.RestoreAsync(storage));
        Assert.Same(store, fixture.State.Library.Store);
        Assert.NotNull(store.ListGames());
    }

    [Fact]
    public async Task Shutdown_DrainsBackgroundLeasesWithoutReopeningAdmission()
    {
        var gate = new LibrarySessionGate();
        var lease = gate.TryEnter(background: true)!;
        var drained = gate.StopAccepting();
        Assert.False(drained.IsCompleted);
        gate.EndMaintenance();
        Assert.Null(gate.TryEnter());
        lease.Dispose();
        await drained;
        Assert.Null(gate.TryBeginMaintenance(() => false));
    }

    [Fact]
    public async Task Maintenance_RejectsBackgroundAndDrainsExistingRequest()
    {
        var gate = new LibrarySessionGate();
        using var background = gate.TryEnter(background: true)!;
        Assert.Null(gate.TryBeginMaintenance(() => false));
        background.Dispose();
        var request = gate.TryEnter()!;
        var drained = gate.TryBeginMaintenance(() => false)!;
        Assert.False(drained.IsCompleted);
        Assert.Null(gate.TryEnter());
        request.Dispose();
        await drained;
        gate.EndMaintenance();
        Assert.NotNull(gate.TryEnter());
    }
}
