using GameLibrary.Host.Hosting;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class JobManagerTests
{
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

    private static async Task WaitForTerminalAsync(JobManager manager, string jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (manager.Get(jobId)?.FinishedUtc is not null)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"作业 {jobId} 未在 60 秒内进入终态");
    }
}
