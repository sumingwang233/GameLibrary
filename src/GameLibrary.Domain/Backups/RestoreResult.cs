namespace GameLibrary.Domain.Backups;

public sealed record RestoreResult(string BackupId, string RestoredLibraryInstanceId, string DataEpoch, string SafetyBackupId, bool Restored = true);
