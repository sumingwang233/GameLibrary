using System.Text.Json;
using GameLibrary.Contracts;

namespace GameLibrary.Host.Hosting;

/// <summary>Session-only spill storage. Never reopen another session's file or delete existing files.</summary>
internal sealed class CompletedJobHistory : IDisposable
{
    internal sealed record Record(JobSnapshot Snapshot, object? Result);
    private readonly object _gate = new();
    // ponytail: history keeps one small offset per job; use an on-disk index if sessions reach millions of jobs.
    private readonly Dictionary<string, (long Offset, int Length)> _index = new(StringComparer.Ordinal);
    private FileStream? _stream;
    internal string? FilePath { get { lock (_gate) return _stream?.Name; } }

    public void Write(JobSnapshot snapshot, object? result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Record(snapshot, result), ContractJson.Options);
        lock (_gate)
        {
            _stream ??= new FileStream(Path.Combine(Path.GetTempPath(), $"GameLibrary-jobs-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
                FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            var offset = _stream.Length;
            _stream.Position = offset;
            _stream.Write(bytes);
            _stream.Flush();
            _index[snapshot.JobId] = (offset, bytes.Length);
        }
    }

    public Record? Read(string jobId)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(jobId, out var entry) || _stream is null) return null;
            _stream.Position = entry.Offset;
            var bytes = new byte[entry.Length];
            _stream.ReadExactly(bytes);
            return JsonSerializer.Deserialize<Record>(bytes, ContractJson.Options)
                ?? throw new InvalidDataException("会话作业结果损坏");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
            _index.Clear();
        }
    }
}
