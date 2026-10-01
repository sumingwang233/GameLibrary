namespace GameLibrary.Domain.Backups;

public sealed record BackupCreationResult(string BackupId, int AssetCount, int FileCount);
