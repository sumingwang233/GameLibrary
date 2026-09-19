using System.Diagnostics;
using GameLibrary.Domain.Tools;
using GameLibrary.Host.Launching;
using Xunit;

namespace GameLibrary.IntegrationTests.Launching;

/// <summary>
/// R39 翻译注入步骤有界等待：挂死的注入工具不得持请求门锁冻结宿主——
/// 超时即终止全部已启动进程、尝试记 processStartFailed、互斥释放。
/// </summary>
public sealed class TranslationStepTimeoutTests
{
    private static string StubExe => Path.Combine(
        AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    private static LaunchRegistry RegistryWithProfile()
    {
        var registry = new LaunchRegistry();
        registry.RestoreProfile(new LaunchProfile
        {
            ProfileId = "profile-timeout",
            GameId = "game-timeout",
            ExecutablePath = StubExe,
            Arguments = [],
            WorkingDirectory = AppContext.BaseDirectory,
        });
        return registry;
    }

    private static RecipeProcessStep Step(int holdMs, bool waitForExit = true) => new()
    {
        ExecutablePath = StubExe,
        Arguments = ["--hold-ms", holdMs.ToString()],
        WorkingDirectory = AppContext.BaseDirectory,
        WaitForExit = waitForExit,
        Sequence = 0,
    };

    [Fact]
    public void Execute_HungTranslationStep_TimesOutKillsProcessesAndFailsAttempt()
    {
        var registry = RegistryWithProfile();
        var steps = new[] { Step(55_000) };

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<LaunchException>(() => registry.Execute(
            "idem-timeout-1",
            planId: null,
            profileId: "profile-timeout",
            expectedRevisionProfileId: null,
            expectedRevision: null,
            translationSteps: steps,
            translationStepTimeout: TimeSpan.FromSeconds(2)));
        stopwatch.Stop();

        Assert.Contains("超时", exception.Message, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"超时应快速返回而非等满挂起时长，实际 {stopwatch.Elapsed}");

        var failed = Assert.Single(registry.History("game-timeout"));
        Assert.Equal("processStartFailed", failed.State);
        Assert.Equal("idem-timeout-1", failed.IdempotencyKey);

        // 互斥已释放：同游戏可再次执行（快速成功步骤验证锁未被挂死尝试占住）。
        var retry = registry.Execute(
            "idem-timeout-2",
            planId: null,
            profileId: "profile-timeout",
            expectedRevisionProfileId: null,
            expectedRevision: null,
            translationSteps: new[] { Step(100) },
            translationStepTimeout: TimeSpan.FromSeconds(10));
        Assert.Equal("processCreated", retry.State);
        StopAttemptProcess(retry.ProcessId);
    }

    [Fact]
    public void Execute_QuickTranslationStep_StillWaitsForCleanExit()
    {
        var registry = RegistryWithProfile();

        var attempt = registry.Execute(
            "idem-quick-1",
            planId: null,
            profileId: "profile-timeout",
            expectedRevisionProfileId: null,
            expectedRevision: null,
            translationSteps: new[] { Step(200) },
            translationStepTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal("processCreated", attempt.State);
        Assert.NotNull(attempt.ProcessId);
        StopAttemptProcess(attempt.ProcessId);
    }

    private static void StopAttemptProcess(int? processId)
    {
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // 进程已自行退出。
        }
    }
}
