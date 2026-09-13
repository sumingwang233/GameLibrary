using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace GameLibrary.Host.Observability;

/// <summary>
/// 业务审计记录（补充规格 4.3）：固定结构化字段；argv/cwd 等隐私参数不默认写入。
/// </summary>
public sealed record AuditRecord
{
    public required DateTime TimestampUtc { get; init; }

    public required string Level { get; init; }

    public required string Component { get; init; }

    public required string OperationId { get; init; }

    public required string RequestId { get; init; }

    public required string Actor { get; init; }

    public string? JobId { get; init; }

    public string? GameId { get; init; }

    public string? ProfileId { get; init; }

    public string? RuleId { get; init; }

    /// <summary>completed / accepted / 错误码。</summary>
    public required string ResultCode { get; init; }

    public long DurationMs { get; init; }

    public string ToJsonLine() => System.Text.Json.JsonSerializer.Serialize(this, GameLibrary.Contracts.ContractJson.Options);
}

/// <summary>
/// 审计日志写入器：JSONL 追加、按大小轮转（默认单文件 10 MiB）、保留上限（默认 10 个）、
/// 过期清理（默认 90 天）。轮换/删除只在日志目录内，不影响库与收据。
/// 业务审计与诊断日志分开：本类文件命名 audit-*.jsonl。
/// </summary>
public sealed class AuditLogWriter
{
    public const long DefaultMaxFileBytes = 10 * 1024 * 1024;
    public const int DefaultMaxFiles = 10;
    public const int DefaultRetentionDays = 90;

    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly int _retentionDays;
    private readonly ConcurrentQueue<AuditRecord> _pending = new();

    public AuditLogWriter(
        string logDirectory,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFiles = DefaultMaxFiles,
        int retentionDays = DefaultRetentionDays)
    {
        _directory = logDirectory;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
    }

    public string LogDirectory => _directory;

    /// <summary>记录当前审计文件名与字节数（diagnostics.status 用）。</summary>
    public (string? CurrentFile, long CurrentBytes, int FileCount) Describe()
    {
        var files = ListAuditFiles();
        if (files.Count == 0)
        {
            return (null, 0, 0);
        }

        var current = files[^1];
        return (Path.GetFileName(current), new FileInfo(current).Length, files.Count);
    }

    public void Append(AuditRecord record)
    {
        _pending.Enqueue(record);
        // 单宿主并发分发：逐条落盘以保持顺序；失败不拖垮业务请求。
        while (_pending.TryDequeue(out var item))
        {
            try
            {
                AppendCore(item);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void AppendCore(AuditRecord record)
    {
        PruneExpired();
        var files = ListAuditFiles();
        var current = files.Count > 0 ? files[^1] : null;
        if (current is not null && new FileInfo(current).Length >= _maxFileBytes)
        {
            current = null;
        }

        current ??= Path.Combine(_directory, $"audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jsonl");
        File.AppendAllText(current, record.ToJsonLine() + Environment.NewLine, Encoding.UTF8);
        PruneFileCount();
    }

    private List<string> ListAuditFiles()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        return [.. System.IO.Directory.EnumerateFiles(_directory, "audit-*.jsonl").OrderBy(f => f, StringComparer.Ordinal)];
    }

    private void PruneFileCount()
    {
        var files = ListAuditFiles();
        for (var i = 0; i < files.Count - _maxFiles; i++)
        {
            try
            {
                File.Delete(files[i]);
            }
            catch (IOException)
            {
            }
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var file in ListAuditFiles())
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>读取最近 count 行（diagnostics.logs）：按文件序、行序返回，已脱敏后再读。</summary>
    public IReadOnlyList<string> ReadRecentLines(int count) =>
        ListAuditFiles()
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0)
            .TakeLast(count)
            .ToArray();
}
