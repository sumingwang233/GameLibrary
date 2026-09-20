using GameLibrary.Host.Hosting;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// 扫描协调器（任务书 T16）：周期核对（预算内小步重扫）、手动/后台互斥、事件发布入口。
/// 周期核对默认 60 分钟，间隔可注入以便测试；核对走与手动扫描相同的
/// ScanJobRunner + 候选落库路径（稳定观察/抑制语义一致）。
/// </summary>
public sealed class ScanCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(60);

    private readonly RootRegistry _roots;
    private readonly EventStream _events;
    private readonly Func<string, JobOutcome> _runReconcileScan;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private int _busy; // 0=空闲 1=核对中（Interlocked）

    /// <summary>手动扫描进行中标志（scan.start 作业置位）：置位期间周期核对跳过（手动/后台互斥）。</summary>
    public volatile bool ManualScanRunning;

    /// <summary>周期核对触发/跳过计数（诊断用）。</summary>
    public long TriggeredCount;

    public long SkippedCount;

    public ScanCoordinator(
        RootRegistry roots,
        EventStream events,
        Func<string, JobOutcome> runReconcileScan,
        TimeSpan? interval = null)
    {
        _roots = roots;
        _events = events;
        _runReconcileScan = runReconcileScan;
        _interval = interval ?? DefaultInterval;
        _timer = new Timer(_ => Tick(), null, _interval, _interval);
    }

    /// <summary>周期核对触发：手动扫描进行中或上一轮未结束时跳过（互斥）。</summary>
    public void Tick()
    {
        if (ManualScanRunning || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Interlocked.Increment(ref SkippedCount);
            return;
        }

        try
        {
            Interlocked.Increment(ref TriggeredCount);
            foreach (var root in _roots.List())
            {
                var outcome = _runReconcileScan(root.Path.PhysicalPath);
                _events.Publish(
                    outcome.FinalState == "succeeded" ? "scan.completed" : "scan.failed",
                    $"reconcile:{root.RootId}",
                    new
                    {
                        rootId = root.RootId,
                        root = root.Path.PhysicalPath,
                        kind = "reconcile",
                        state = outcome.FinalState,
                        error = outcome.Error,
                    },
                    DateTime.UtcNow);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>候选/游戏事件发布（由 dispatcher 在落库与审核路径调用）。</summary>
    public void Publish(string type, string entityKey, object payload) =>
        _events.Publish(type, entityKey, payload, DateTime.UtcNow);

    public void Dispose() => _timer.Dispose();
}
