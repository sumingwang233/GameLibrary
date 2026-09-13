using GameLibrary.Host.Hosting;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class JobManagerTests
{
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
        Assert.Equal("cancelRequested", manager.Get(jobId)!.State);
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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (manager.Get(jobId)?.FinishedUtc is not null)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"作业 {jobId} 未在 10 秒内进入终态");
    }
}
