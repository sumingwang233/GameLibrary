using System.Collections.Concurrent;

namespace GameLibrary.Host.Hosting;

/// <summary>作业终态判定结果（执行器返回）。</summary>
public sealed record JobOutcome(string FinalState, string? Error = null)
{
    public static JobOutcome Succeeded() => new("succeeded");

    public static JobOutcome Failed(string error) => new("failed", error);

    public static JobOutcome Cancelled() => new("cancelled");
}

/// <summary>执行器上下文：作业 ID、取消令牌与进度数据写入（供 coverage 类查询实时读取）。</summary>
public sealed class JobContext
{
    public required string JobId { get; init; }

    public required CancellationToken Token { get; init; }

    private object? _progress;

    public void ReportProgress(object data) => Volatile.Write(ref _progress, data);

    public object? ReadProgress() => Volatile.Read(ref _progress);
}

public sealed record JobSnapshot
{
    public required string JobId { get; init; }

    public required string Kind { get; init; }

    /// <summary>queued/running/cancelRequested/cancelled/succeeded/failed（契约第 8 节子集）。</summary>
    public required string State { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? FinishedUtc { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// 宿主内作业管理（T05 骨架）：受受理即后台执行；取消只置 cancelRequested 并在检查点生效；
/// 客户端断开不影响作业。跨进程重启的持久化恢复属 T23/T27。
/// </summary>
public sealed class JobManager
{
    private sealed class JobEntry
    {
        public required string Id { get; init; }

        public required string Kind { get; init; }

        public volatile string State = "queued";

        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public DateTime? StartedUtc;

        public DateTime? FinishedUtc;

        public string? Error;

        public CancellationTokenSource Cts { get; } = new();

        public JobContext? Context;

        public object? FinalData;
    }

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

    /// <summary>作业终态指标回调（T24-B）：进入 succeeded/failed/cancelled 时调用，参数为终态字符串。</summary>
    public Action<string>? OnJobFinished { get; set; }

    /// <summary>
    /// 作业记录回调（v1 审查修复：作业落库）——创建（running）与进入终态时各触发一次，
    /// Host 接线到运行态持久层；跨重启的作业历史与中断标记由此获得。
    /// </summary>
    public Action<JobSnapshot>? OnJobRecorded { get; set; }

    private void Record(JobEntry entry)
    {
        lock (entry)
        {
            OnJobRecorded?.Invoke(Snapshot(entry));
        }
    }

    public string Create(
        string kind,
        Func<JobContext, Task<JobOutcome>> executor,
        object? initialProgress = null)
    {
        var entry = new JobEntry { Id = $"job-{Guid.NewGuid():N}", Kind = kind };
        _jobs[entry.Id] = entry;
        entry.Context = new JobContext { JobId = entry.Id, Token = entry.Cts.Token };
        if (initialProgress is not null)
        {
            entry.Context.ReportProgress(initialProgress);
        }

        entry.State = "running";
        entry.StartedUtc = DateTime.UtcNow;
        Record(entry);

        // Executors may perform synchronous filesystem work before returning a Task. Always
        // cross a thread-pool boundary so accepting a job never blocks the IPC request thread.
        _ = Task.Run(() => RunAsync(entry, executor));
        return entry.Id;
    }

    public JobSnapshot? Get(string jobId) =>
        _jobs.TryGetValue(jobId, out var entry)
            ? Snapshot(entry)
            : null;

    /// <summary>请求取消：仅 running→cancelRequested；已进入终态返回 false。</summary>
    public bool RequestCancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            if (entry.State is not ("running" or "queued"))
            {
                return false;
            }

            entry.State = "cancelRequested";
        }

        entry.Cts.Cancel();
        return true;
    }

    /// <summary>读取作业进度数据（运行中=实时，已结束=最终结果）。</summary>
    public (string State, object? Data)? TryGetProgress(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return null;
        }

        return IsTerminal(entry.State)
            ? (entry.State, entry.FinalData)
            : (entry.State, entry.Context?.ReadProgress());
    }

    /// <summary>活动（非终态）作业数：diagnostics.status 的队列指标。</summary>
    public int ActiveJobCount() =>
        _jobs.Values.Count(entry => !IsTerminal(entry.State));

    private static bool IsTerminal(string state) =>
        state is "succeeded" or "failed" or "cancelled";

    private async Task RunAsync(JobEntry entry, Func<JobContext, Task<JobOutcome>> executor)
    {
        try
        {
            var outcome = await executor(entry.Context!);
            entry.FinalData = entry.Context!.ReadProgress();
            entry.State = outcome.FinalState;
            entry.Error = outcome.Error;
        }
        catch (OperationCanceledException) when (entry.Cts.IsCancellationRequested)
        {
            entry.State = "cancelled";
        }
        catch (Exception ex)
        {
            entry.State = "failed";
            entry.Error = ex.Message;
        }
        finally
        {
            entry.FinishedUtc = DateTime.UtcNow;
            Record(entry);
            OnJobFinished?.Invoke(entry.State);
        }
    }

    private static JobSnapshot Snapshot(JobEntry entry) =>
        new()
        {
            JobId = entry.Id,
            Kind = entry.Kind,
            State = entry.State,
            CreatedUtc = entry.CreatedUtc,
            StartedUtc = entry.StartedUtc,
            FinishedUtc = entry.FinishedUtc,
            Error = entry.Error,
        };
}
