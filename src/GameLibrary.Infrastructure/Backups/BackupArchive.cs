using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLibrary.Infrastructure.Backups;

/// <summary>备份清单（补充规格 4.2）：格式版本、来源实例、逐文件大小与 SHA-256。</summary>
public sealed record BackupManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string BackupId { get; init; }

    public required string LibraryInstanceId { get; init; }

    /// <summary>备份时的 dataEpoch；恢复后会被重新续期（恢复生成新 epoch）。</summary>
    public required string SourceDataEpoch { get; init; }

    public required string AppVersion { get; init; }

    public required int SchemaVersion { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required IReadOnlyList<BackupFileEntry> Files { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, ManifestOptions);

    public static BackupManifest? FromJson(string json) =>
        JsonSerializer.Deserialize<BackupManifest>(json, ManifestOptions);

    /// <summary>Infrastructure 不引用 Contracts；清单序列化选项本地固定（camelCase）。</summary>
    public static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web);
}

public sealed record BackupFileEntry(string RelativePath, long SizeBytes, string Sha256);

/// <summary>
/// 备份归档（ADR-0004）：备份目录 = library.db（SQLite 备份 API 一致副本）+ assets/（用户原图）+ manifest.json。
/// 缓存与外部游戏不在备份内。控制区（控制收据/维护日志）不在备份内——它们必须比业务库活得久。
/// </summary>
public static class BackupArchive
{
    public const string ManifestFileName = "manifest.json";
    public const string DatabaseFileName = "library.db";
    public const string AssetsFolderName = "assets";

    public static string BackupDirectory(string backupsRoot, string backupId) =>
        Path.Combine(backupsRoot, backupId);

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>写入清单（逐文件哈希在写入时计算）。</summary>
    public static void WriteManifest(string backupDirectory, BackupManifest manifest)
    {
        var payload = manifest.ToJson();
        File.WriteAllText(Path.Combine(backupDirectory, ManifestFileName), payload);
    }

    public static BackupManifest? TryReadManifest(string backupDirectory)
    {
        var path = Path.Combine(backupDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return BackupManifest.FromJson(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 完整性校验：清单存在、文件齐全、大小与 SHA-256 逐一对得上。
    /// 返回问题列表；空列表即备份完整。
    /// </summary>
    public static IReadOnlyList<string> Verify(string backupDirectory, BackupManifest manifest)
    {
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            var fullPath = Path.Combine(backupDirectory, file.RelativePath);
            if (!File.Exists(fullPath))
            {
                problems.Add($"缺失：{file.RelativePath}");
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.SizeBytes)
            {
                problems.Add($"大小不符：{file.RelativePath}");
                continue;
            }

            if (!string.Equals(ComputeSha256(fullPath), file.Sha256, StringComparison.Ordinal))
            {
                problems.Add($"哈希不符：{file.RelativePath}");
            }
        }

        return problems;
    }

    /// <summary>目录复制（递归），返回已复制文件数。</summary>
    public static int CopyDirectory(string sourceDir, string destDir)
    {
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir, StringComparison.OrdinalIgnoreCase));
        }

        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = file.Replace(sourceDir, destDir, StringComparison.OrdinalIgnoreCase);
            File.Copy(file, dest, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>备份清单的文件枚举（library.db + assets 递归）。</summary>
    public static IReadOnlyList<BackupFileEntry> EnumerateFiles(string backupDirectory)
    {
        var files = new List<BackupFileEntry>
        {
            new(DatabaseFileName, new FileInfo(Path.Combine(backupDirectory, DatabaseFileName)).Length, ComputeSha256(Path.Combine(backupDirectory, DatabaseFileName))),
        };

        var assetsDir = Path.Combine(backupDirectory, AssetsFolderName);
        if (Directory.Exists(assetsDir))
        {
            foreach (var file in Directory.EnumerateFiles(assetsDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(backupDirectory, file).Replace('\\', '/');
                var info = new FileInfo(file);
                files.Add(new BackupFileEntry(relative, info.Length, ComputeSha256(file)));
            }
        }

        return files;
    }
}

/// <summary>
/// 控制区（契约第 10 节）：控制收据与维护日志存放在 LocalData/control，
/// **不随业务库回滚**——否则恢复重试会找不到原收据再次覆盖。
/// v1 采用长度受限、Flush 落盘的 JSON 文件 + 追加日志，非第二个业务数据库。
/// </summary>
public sealed class ControlAreaStore
{
    public ControlAreaStore(string controlDirectory)
    {
        ControlDirectory = controlDirectory;
        Directory.CreateDirectory(controlDirectory);
        Directory.CreateDirectory(ReceiptsDirectory);
    }

    public string ControlDirectory { get; }

    public string ReceiptsDirectory => Path.Combine(ControlDirectory, "receipts");

    public string MaintenanceLogPath => Path.Combine(ControlDirectory, "maintenance.log");

    /// <summary>恢复类控制收据：同键存在即返回（重试不再覆盖）；不同摘要 → 错误。</summary>
    public (string? ExistingResultJson, string? Conflict) BeginRestoreReceipt(string idempotencyKey, string requestDigest)
    {
        var path = Path.Combine(ReceiptsDirectory, $"restore-{idempotencyKey}.json");
        System.IO.File.AppendAllText("D:/Official/GameLibrary/artifacts/receipt-debug.log", $"{DateTime.UtcNow:O} begin key={idempotencyKey} exists={File.Exists(path)}\n");
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var digest = document.RootElement.GetProperty("requestDigest").GetString();
                System.IO.File.AppendAllText("D:/Official/GameLibrary/artifacts/receipt-debug.log", $"{DateTime.UtcNow:O} stored digest match={string.Equals(digest, requestDigest, StringComparison.Ordinal)} status={document.RootElement.GetProperty("status").GetString()}\n");
                if (!string.Equals(digest, requestDigest, StringComparison.Ordinal))
                {
                    return (null, "幂等键已被不同恢复请求使用");
                }

                var resultJson = document.RootElement.TryGetProperty("resultJson", out var rj) ? rj.GetString() : null;
                System.IO.File.AppendAllText("D:/Official/GameLibrary/artifacts/receipt-debug.log", $"{DateTime.UtcNow:O} existing result null={resultJson is null}\n");
                return (resultJson, null);
            }
            catch (JsonException)
            {
                return (null, "控制收据损坏");
            }
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new { requestDigest, status = "prepared", createdUtc = DateTime.UtcNow.ToString("O") }),
            System.Text.Encoding.UTF8);
        System.IO.File.AppendAllText("D:/Official/GameLibrary/artifacts/receipt-debug.log", $"{DateTime.UtcNow:O} prepared written\n");
        return (null, null);
    }

    public void CompleteRestoreReceipt(string idempotencyKey, string requestDigest, string resultJson)
    {
        var path = Path.Combine(ReceiptsDirectory, $"restore-{idempotencyKey}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new { requestDigest, status = "completed", resultJson, updatedUtc = DateTime.UtcNow.ToString("O") }),
            System.Text.Encoding.UTF8);
    }

    /// <summary>读取已完成恢复的收据结果；不存在返回 null。</summary>
    public string? TryReadRestoreResult(string idempotencyKey)
    {
        var path = Path.Combine(ReceiptsDirectory, $"restore-{idempotencyKey}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.GetProperty("status").GetString() != "completed")
            {
                return null;
            }

            return document.RootElement.GetProperty("resultJson").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>维护日志追加（恢复/切换的每一步落盘，进程崩溃后可据此恢复状态）。</summary>
    public void AppendMaintenanceLog(string step)
    {
        File.AppendAllText(
            MaintenanceLogPath,
            $"{DateTime.UtcNow:O}\t{step}{Environment.NewLine}",
            System.Text.Encoding.UTF8);
    }
}
