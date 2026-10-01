using System.Text.Json;
using GameLibrary.Application.Backups;
using GameLibrary.Domain.Backups;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Infrastructure.Backups;

public sealed record RestoreJournal(
    string StageName, string BackupId, string SafetyBackupId, string IdempotencyKey,
    string RequestDigest, bool HadAssets, string Phase, RestoreResult? Result = null, string? JobId = null);

/// <summary>A flushed control journal is the commit point for the database and asset directory.</summary>
public sealed class LibraryRestoreStorage : IRestoreStorage
{
    private readonly string _dataDirectory;
    private readonly string _backupDirectory;
    private readonly BackupManifest _manifest;
    private readonly SqliteLibraryStore _previous;
    private readonly SqliteLibraryStoreOptions _options;
    private readonly Action<SqliteLibraryStore?> _publish;
    private readonly Action _detach;
    private RestoreJournal _journal;
    private SqliteLibraryStore? _opened;
    private bool _prepared;
    private bool _detached;

    public Action<string>? Checkpoint { get; init; }

    public LibraryRestoreStorage(string dataDirectory, string backupDirectory, BackupManifest manifest,
        SqliteLibraryStore previous, SqliteLibraryStoreOptions options, string key, string digest,
        Action detach, Action<SqliteLibraryStore?> publish, string? jobId = null)
    {
        _dataDirectory = dataDirectory;
        _backupDirectory = backupDirectory;
        _manifest = manifest;
        _previous = previous;
        _options = options;
        _detach = detach;
        _publish = publish;
        var id = Guid.NewGuid().ToString("N");
        _journal = new($".restore-{id}", manifest.BackupId, $"pre-restore-{id}", key, digest,
            Directory.Exists(Path.Combine(dataDirectory, "assets")), "prepared", JobId: jobId);
    }

    private string Stage => Path.Combine(_dataDirectory, _journal.StageName);
    private string SafetyDb => Path.Combine(_dataDirectory, "backups", _journal.SafetyBackupId, "library.db");

    public async Task PrepareAsync()
    {
        if (!_manifest.Files.Any(file => file.RelativePath == "library.db"))
            throw new InvalidOperationException("备份清单缺少 library.db");
        var problems = BackupArchive.Verify(_backupDirectory, _manifest);
        if (problems.Count != 0) throw new InvalidOperationException(string.Join("; ", problems));
        Directory.CreateDirectory(Stage);
        CopyFlushed(Path.Combine(_backupDirectory, "library.db"), Path.Combine(Stage, "library.db"));
        var incoming = Path.Combine(Stage, "assets");
        Directory.CreateDirectory(incoming);
        var assets = Path.Combine(_backupDirectory, "assets");
        if (Directory.Exists(assets)) BackupArchive.CopyDirectory(assets, incoming);
        foreach (var file in Directory.EnumerateFiles(incoming, "*", SearchOption.AllDirectories)) FlushFile(file);
        var stagedProblems = BackupArchive.Verify(Stage, _manifest);
        if (stagedProblems.Count != 0) throw new InvalidOperationException(string.Join("; ", stagedProblems));
        await _previous.CreateBackupAsync(SafetyDb, CancellationToken.None);
        FlushFile(SafetyDb);
        WriteJournal(_dataDirectory, _journal);
        _prepared = true;
        Checkpoint?.Invoke("prepared");
    }

    public async Task SwapAsync()
    {
        _detach();
        _detached = true;
        await _previous.DisposeAsync();
        RemoveWal(_dataDirectory);
        File.Replace(Path.Combine(Stage, "library.db"), Path.Combine(_dataDirectory, "library.db"), Path.Combine(Stage, "previous.db"));
        RecordPhase("database-swapped");
        var assets = Path.Combine(_dataDirectory, "assets");
        if (Directory.Exists(assets)) Directory.Move(assets, Path.Combine(Stage, "previous-assets"));
        Directory.Move(Path.Combine(Stage, "assets"), assets);
        RecordPhase("assets-swapped");
    }

    private void RecordPhase(string phase)
    {
        var journal = _journal with { Phase = phase };
        WriteJournal(_dataDirectory, journal);
        _journal = journal;
        Checkpoint?.Invoke(phase);
    }

    public async Task<RestoreResult> ValidateAsync()
    {
        var opened = await SqliteLibraryStore.TryOpenAsync(_dataDirectory, _options, CancellationToken.None);
        if (!opened.IsOpened) throw new InvalidOperationException($"恢复库无法打开：{opened.Detail}");
        _opened = opened.Store!;
        _opened.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = FULL";
            command.ExecuteNonQuery();
        });
        var epoch = await _opened.RenewDataEpochAsync(CancellationToken.None);
        _opened.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        });
        using (var database = new FileStream(Path.Combine(_dataDirectory, "library.db"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite)) database.Flush(flushToDisk: true);
        return new(_journal.BackupId, _opened.Info.LibraryInstanceId, epoch, _journal.SafetyBackupId);
    }

    public void Commit(RestoreResult result)
    {
        var committed = _journal with { Phase = "committed", Result = result };
        WriteJournal(_dataDirectory, committed);
        _journal = committed;
        Checkpoint?.Invoke("committed");
        _publish(_opened);
    }

    public async Task RollbackAsync()
    {
        if (!_prepared || _journal.Phase == "committed") return;
        if (_opened is not null) await _opened.DisposeAsync();
        if (_detached)
        {
            RollbackFiles(_dataDirectory, _journal);
            var old = await SqliteLibraryStore.TryOpenAsync(_dataDirectory, _options, CancellationToken.None);
            if (!old.IsOpened) throw new IOException($"恢复失败且原库无法重开：{old.Detail}");
            _publish(old.Store);
        }
        _journal = _journal with { Phase = "rolled-back" };
        WriteJournal(_dataDirectory, _journal);
    }

    public static RestoreJournal? ReadJournal(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "control", "restore-in-progress.json");
        if (!File.Exists(path)) return null;
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(path))
            ?? throw new IOException("恢复控制记录为空");
        if (journal.Phase is not ("prepared" or "database-swapped" or "assets-swapped" or "committed" or "rolled-back")
            || (journal.Phase == "committed" && journal.Result is null))
            throw new IOException("恢复控制记录状态非法");
        return journal;
    }

    public static void RecoverBeforeOpen(string dataDirectory)
    {
        var journal = ReadJournal(dataDirectory);
        if (journal is null || journal.Phase is "committed" or "rolled-back") return;
        RollbackFiles(dataDirectory, journal);
        WriteJournal(dataDirectory, journal with { Phase = "rolled-back" });
    }

    private static void RollbackFiles(string dataDirectory, RestoreJournal journal)
    {
        // Journal paths are identifiers, never arbitrary paths supplied by a manifest.
        if (!journal.StageName.StartsWith(".restore-", StringComparison.Ordinal)
            || !Guid.TryParseExact(journal.StageName[9..], "N", out _)
            || !BackupArchive.IsValidBackupId(journal.SafetyBackupId)) throw new IOException("恢复控制记录路径非法");
        var stage = Path.Combine(dataDirectory, journal.StageName);
        var safety = Path.Combine(dataDirectory, "backups", journal.SafetyBackupId, "library.db");
        if (!File.Exists(safety)) throw new IOException("恢复安全备份缺失");
        RemoveWal(dataDirectory);
        var temporary = Path.Combine(stage, "rollback.db");
        CopyFlushed(safety, temporary);
        var target = Path.Combine(dataDirectory, "library.db");
        if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        var assets = Path.Combine(dataDirectory, "assets");
        var previous = Path.Combine(stage, "previous-assets");
        if (journal.HadAssets && !Directory.Exists(previous)
            && (journal.Phase == "assets-swapped" || !Directory.Exists(assets)))
            throw new IOException("恢复前的资产目录缺失，无法安全回滚");
        if (Directory.Exists(previous) || !journal.HadAssets)
        {
            string? rollbackAssets = null;
            if (Directory.Exists(previous))
            {
                rollbackAssets = Path.Combine(stage, $"rollback-assets-{Guid.NewGuid():N}");
                BackupArchive.CopyDirectory(previous, rollbackAssets);
                foreach (var file in Directory.EnumerateFiles(rollbackAssets, "*", SearchOption.AllDirectories)) FlushFile(file);
            }
            if (Directory.Exists(assets)) Directory.Move(assets, Path.Combine(stage, $"discarded-assets-{Guid.NewGuid():N}"));
            if (rollbackAssets is not null) Directory.Move(rollbackAssets, assets);
        }
    }

    private static void WriteJournal(string dataDirectory, RestoreJournal journal) =>
        ControlAreaStore.WriteAtomic(Path.Combine(dataDirectory, "control", "restore-in-progress.json"), JsonSerializer.Serialize(journal));

    public static void CopyFlushed(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void FlushFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Flush(flushToDisk: true);
    }

    private static void RemoveWal(string directory)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = Path.Combine(directory, "library.db" + suffix);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
