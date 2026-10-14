using GameLibrary.Host.Hosting;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class JobManagerTests
{
    [Fact]
    public async Task RestoreHistory_FinalWriteFailureCanBeRetriedWithoutRepeatingRestore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-restore-final-" + Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new JobManager();
        manager.ConfigureRestoreHistory(directory);
        var executions = 0;
        try
        {
            var id = manager.Create("restore", async context =>
            {
                Interlocked.Increment(ref executions);
                await release.Task;
                context.ReportProgress("original restore result");
                return JobOutcome.Succeeded();
            }, ownsMaintenance: true);
            var history = Path.Combine(directory, "control", "restore-jobs", id + ".json");
            using (var locked = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                release.TrySetResult();
                await manager.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("failed", manager.Get(id)!.State);
                Assert.Equal(1, manager.PendingRecordCount);
                Assert.False(manager.TryFlushPendingRecords());
            }
            Assert.True(manager.TryFlushPendingRecords());
            Assert.Equal(0, manager.PendingRecordCount);
            Assert.Equal(1, executions);
            var restarted = new JobManager();
            restarted.ConfigureRestoreHistory(directory);
            Assert.Equal("succeeded", restarted.Get(id)!.State);
            Assert.Equal("original restore result", restarted.TryGetProgress(id)!.Value.Data!.ToString());
        }
        finally
        {
            release.TrySetResult();
            await manager.WaitForIdleAsync();
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalState_IsPublishedAfterFinalRecord()
    {
        var manager = new JobManager();
        var recording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        manager.OnJobRecorded = snapshot =>
        {
            if (snapshot.State != "succeeded") return;
            recording.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var jobId = manager.Create("test", _ => Task.FromResult(JobOutcome.Succeeded()));
        try
        {
            await recording.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("running", manager.Get(jobId)!.State);
            Assert.Null(manager.Get(jobId)!.FinishedUtc);
            Assert.Equal("running", manager.TryGetProgress(jobId)!.Value.State);
            Assert.Equal(1, manager.ActiveJobCount());
        }
        finally { release.Set(); }

        await manager.WaitForIdleAsync();
        Assert.Equal("succeeded", manager.Get(jobId)!.State);
        Assert.Equal(0, manager.ActiveJobCount());
    }

    [Fact]
    public async Task Create_DoesNotWaitForSynchronousExecutor()
    {
        var manager = new JobManager();
        using var release = new ManualResetEventSlim(false);

        var createTask = Task.Run(() => manager.Create(
            "blocking-test",
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return Task.FromResult(JobOutcome.Succeeded());
            }));

        try
        {
            var completed = await Task.WhenAny(createTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(createTask, completed);

            var jobId = await createTask;
            Assert.Equal("running", manager.Get(jobId)!.State);
            release.Set();
            await WaitForTerminalAsync(manager, jobId);
            Assert.Equal("succeeded", manager.Get(jobId)!.State);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Create_RunsToSucceeded()
    {
        var manager = new JobManager();

        var jobId = manager.Create("test", _ => Task.FromResult(JobOutcome.Succeeded()));
        await WaitForTerminalAsync(manager, jobId);

        var snapshot = manager.Get(jobId);
        Assert.NotNull(snapshot);
        Assert.Equal("succeeded", snapshot!.State);
        Assert.Equal("test", snapshot.Kind);
        Assert.NotNull(snapshot.FinishedUtc);
    }

    [Fact]
    public async Task Cancel_BlocksUntilCheckpoint_ThenCancelled()
    {
        var manager = new JobManager();

        var jobId = manager.Create(
            "test",
            async context =>
            {
                await Task.Delay(Timeout.Infinite, context.Token);
                return JobOutcome.Succeeded();
            });

        Assert.Equal("running", manager.Get(jobId)!.State);
        Assert.True(manager.RequestCancel(jobId));
        // 取消是请求语义：状态机必须经过 cancelRequested（响应里可见），
        // 随后在检查点生效转为 cancelled；两者到达顺序由执行器调度决定，均合法。
        var stateAfterRequest = manager.Get(jobId)!.State;
        Assert.True(stateAfterRequest is "cancelRequested" or "cancelled", $"实际 {stateAfterRequest}");
        await WaitForTerminalAsync(manager, jobId);
        Assert.Equal("cancelled", manager.Get(jobId)!.State);
    }

    [Fact]
    public async Task Cancel_TerminalJob_ReturnsFalse()
    {
        var manager = new JobManager();
        var jobId = manager.Create("test", _ => Task.FromResult(JobOutcome.Succeeded()));
        await WaitForTerminalAsync(manager, jobId);

        Assert.False(manager.RequestCancel(jobId));
    }

    [Fact]
    public void Cancel_UnknownJob_ReturnsFalse()
    {
        Assert.False(new JobManager().RequestCancel("job-missing"));
    }

    [Fact]
    public async Task Executor_Exception_FailsJobWithError()
    {
        var manager = new JobManager();
        var jobId = manager.Create(
            "test",
            _ => throw new InvalidOperationException("boom"));

        await WaitForTerminalAsync(manager, jobId);
        var snapshot = manager.Get(jobId)!;
        Assert.Equal("failed", snapshot.State);
        Assert.Contains("boom", snapshot.Error);
    }

    [Fact]
    public async Task Progress_IsReadableWhileRunningAndAfterFinish()
    {
        var manager = new JobManager();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();

        var jobId = manager.Create(
            "test",
            async context =>
            {
                context.ReportProgress(new { step = 1 });
                started.SetResult();
                await finish.Task;
                context.ReportProgress(new { step = 2 });
                return JobOutcome.Succeeded();
            });

        await started.Task;
        var running = manager.TryGetProgress(jobId);
        Assert.Equal("running", running!.Value.State);
        finish.SetResult();
        await WaitForTerminalAsync(manager, jobId);

        var final = manager.TryGetProgress(jobId);
        Assert.Equal("succeeded", final!.Value.State);
    }

    [Fact]
    public async Task FailedFinalRecord_IsObservableAndRetriedWithoutRepeatingWork()
    {
        var manager = new JobManager();
        var fail = true;
        var executions = 0;
        var recorded = new List<JobSnapshot>();
        manager.OnJobRecorded = snapshot =>
        {
            if (snapshot.State == "succeeded" && fail) throw new IOException("disk full");
            recorded.Add(snapshot);
        };
        manager.OnJobFinished = _ => throw new InvalidOperationException("metrics unavailable");
        var result = new { value = 7 };
        var id = manager.Create("test", context =>
        {
            executions++;
            context.ReportProgress(result);
            return Task.FromResult(JobOutcome.Succeeded());
        });
        await manager.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("failed", manager.Get(id)!.State);
        Assert.Contains("disk full", manager.Get(id)!.Error);
        Assert.Equal(0, manager.ActiveJobCount());
        Assert.Equal(1, manager.PendingRecordCount);
        Assert.Same(result, manager.TryGetProgress(id)!.Value.Data);
        Assert.False(manager.TryFlushPendingRecords());
        fail = false;
        Assert.True(manager.TryFlushPendingRecords());
        Assert.True(manager.TryFlushPendingRecords());
        Assert.Equal(0, manager.PendingRecordCount);
        Assert.Equal("succeeded", manager.Get(id)!.State);
        Assert.Equal(1, executions);
        Assert.Single(recorded, snapshot => snapshot.State == "succeeded");
    }

    private static async Task WaitForTerminalAsync(JobManager manager, string jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (manager.Get(jobId)?.State is "succeeded" or "failed" or "cancelled")
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"作业 {jobId} 未在 60 秒内进入终态");
    }
}
