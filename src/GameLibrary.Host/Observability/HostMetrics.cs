using System.Collections.Concurrent;

namespace GameLibrary.Host.Observability;

/// <summary>
/// 宿主运行指标（T24-B，补充规格 4.3）：请求延迟（总数/平均/最大 + 按错误码计数）、
/// 事件流计数（发布/折叠/淘汰）、作业终态计数。全部内存态，随进程生命周期；
/// 经 diagnostics.status 暴露，不做聚合导出。
/// </summary>
public sealed class HostMetrics
{
    private long _requestsTotal;
    private long _requestsFailed;
    private long _latencyTotalMs;
    private long _latencyMaxMs;
    private readonly ConcurrentDictionary<string, long> _errorsByCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _requestsByOperation = new(StringComparer.Ordinal);

    private long _eventsPublished;
    private long _eventsCoalesced;
    private long _eventsOverflowed;
    private long _jobsSucceeded;
    private long _jobsFailed;
    private long _jobsCancelled;

    public void RecordRequest(string operationId, bool ok, string? errorCode, long durationMs)
    {
        Interlocked.Increment(ref _requestsTotal);
        if (!ok)
        {
            Interlocked.Increment(ref _requestsFailed);
            if (errorCode is not null)
            {
                _errorsByCode.AddOrUpdate(errorCode, 1, (_, count) => count + 1);
            }
        }

        _requestsByOperation.AddOrUpdate(operationId, 1, (_, count) => count + 1);
        Interlocked.Add(ref _latencyTotalMs, durationMs);
        var currentMax = Interlocked.Read(ref _latencyMaxMs);
        if (durationMs > currentMax)
        {
            Interlocked.Exchange(ref _latencyMaxMs, durationMs);
        }
    }

    public void RecordEventPublished(bool coalesced, bool overflowed)
    {
        Interlocked.Increment(ref _eventsPublished);
        if (coalesced)
        {
            Interlocked.Increment(ref _eventsCoalesced);
        }

        if (overflowed)
        {
            Interlocked.Increment(ref _eventsOverflowed);
        }
    }

    public void RecordJobFinished(string finalState)
    {
        switch (finalState)
        {
            case "succeeded":
                Interlocked.Increment(ref _jobsSucceeded);
                break;
            case "failed":
                Interlocked.Increment(ref _jobsFailed);
                break;
            case "cancelled":
                Interlocked.Increment(ref _jobsCancelled);
                break;
        }
    }

    public object ToDto()
    {
        var total = Interlocked.Read(ref _requestsTotal);
        var latencyTotal = Interlocked.Read(ref _latencyTotalMs);
        return new
        {
            requests = new
            {
                total = total,
                failed = Interlocked.Read(ref _requestsFailed),
                avgDurationMs = total == 0 ? 0 : latencyTotal / total,
                maxDurationMs = Interlocked.Read(ref _latencyMaxMs),
                byOperation = _requestsByOperation.ToDictionary(p => p.Key, p => p.Value),
                errorsByCode = _errorsByCode.ToDictionary(p => p.Key, p => p.Value),
            },
            events = new
            {
                published = Interlocked.Read(ref _eventsPublished),
                coalesced = Interlocked.Read(ref _eventsCoalesced),
                overflowed = Interlocked.Read(ref _eventsOverflowed),
            },
            jobs = new
            {
                succeeded = Interlocked.Read(ref _jobsSucceeded),
                failed = Interlocked.Read(ref _jobsFailed),
                cancelled = Interlocked.Read(ref _jobsCancelled),
            },
        };
    }
}
