namespace GameLibrary.Host.Hosting;

/// <summary>Protects a library generation from being disposed while requests or jobs use it.</summary>
public sealed class LibrarySessionGate
{
    private readonly object _sync = new();
    private int _leases;
    private int _background;
    private bool _maintenance;
    private bool _stopping;
    private TaskCompletionSource _drained = CompletedSource();

    public bool IsInMaintenance { get { lock (_sync) return _maintenance; } }

    public IDisposable? TryEnter(bool background = false)
    {
        lock (_sync)
        {
            if (_maintenance) return null;
            if (_leases++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (background) _background++;
            return new Lease(this, background);
        }
    }

    public Task? TryBeginMaintenance(Func<bool> isBusy)
    {
        lock (_sync)
        {
            if (_maintenance || _background != 0 || isBusy()) return null;
            _maintenance = true;
            return _drained.Task;
        }
    }

    public void EndMaintenance() { lock (_sync) _maintenance = _stopping; }

    public Task StopAccepting()
    {
        lock (_sync)
        {
            _stopping = _maintenance = true;
            return _drained.Task;
        }
    }

    private void Release(bool background)
    {
        lock (_sync)
        {
            if (background) _background--;
            if (--_leases == 0) _drained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Lease(LibrarySessionGate owner, bool background) : IDisposable
    {
        private LibrarySessionGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(background);
    }
}
