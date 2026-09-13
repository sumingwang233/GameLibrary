using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Tools;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Tools;

/// <summary>
/// T08 RenpyThief：只读安装发现（主程序/launcher/教程标记 → 指纹与 Guided 计划，无参数）、
/// 验证记录生命周期（start → 双结论 report → VerifiedAutomatic；指纹失效；invalidate）。
/// 全程只读，不启动任何进程。
/// </summary>
public sealed class RenpyThiefTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public RenpyThiefTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "rt-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters);
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    private static string CreateFakeInstall(string prefix)
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "trans", "launcher"));
        File.WriteAllText(Path.Combine(root, "RenpyThief.exe"), "stub");
        File.WriteAllText(Path.Combine(root, "trans", "launcher", "RenpyThiefLauncher.exe"), "stub");
        File.WriteAllText(Path.Combine(root, "视频教程链接.txt"), "https://example.com/guide");
        return root;
    }

    [Fact]
    public void Discover_FindsInstall_BindsFingerprintAndGuidedPlan()
    {
        var root = CreateFakeInstall("rt-found");
        try
        {
            var discovery = new RenpyThiefAdapter().Discover(root);

            Assert.True(discovery.Found);
            Assert.Equal(64, discovery.Fingerprint!.Length);
            Assert.Equal(Path.Combine(root, "RenpyThief.exe"), discovery.MainExecutablePath);
            Assert.NotNull(discovery.LauncherPath);
            Assert.NotNull(discovery.GuidedPlan);
            // Guided 计划：无参数（不猜测协议），工作目录=安装目录。
            Assert.Empty(discovery.GuidedPlan!.Arguments);
            Assert.Equal(root, discovery.GuidedPlan.WorkingDirectory);
            Assert.Equal("Unsupported", discovery.Capability["canRequestInjection"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Discover_EmptyDirectory_ReportsNotFound()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"rt-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var discovery = new RenpyThiefAdapter().Discover(root);
            Assert.False(discovery.Found);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task VerificationLifecycle_Start_ReportFingerprintInvalid()
    {
        var root = CreateFakeInstall("rt-verif");
        try
        {
            var discovery = new RenpyThiefAdapter().Discover(root);
            var fingerprint = discovery.Fingerprint!;

            var start = await InvokeAsync("verification.start", new
            {
                idempotencyKey = "v-start",
                toolId = "renpythief",
                fingerprint,
                engine = "renpy",
                samplePath = @"D:\Official\GameLibrary\artifacts\test-runs\sandbox",
            });
            Assert.True(start.Ok, start.Error?.Message);
            Assert.Equal("unknown", start.Data.GetProperty("status").GetString());
            var recordId = start.Data.GetProperty("recordId").GetString()!;

            // 孤立翻译证据：不升级（必须先有游戏启动证据）。
            var bad = await InvokeAsync("verification.report", new
            {
                idempotencyKey = "v-bad",
                recordId,
                translationConfirmed = true,
            });
            Assert.True(bad.Ok);
            Assert.Equal("unknown", bad.Data.GetProperty("status").GetString());

            // 游戏启动 → SemiAutomatic。
            var started = await InvokeAsync("verification.report", new
            {
                idempotencyKey = "v-started",
                recordId,
                gameStarted = true,
            });
            Assert.True(started.Ok);
            Assert.Equal("semiAutomatic", started.Data.GetProperty("status").GetString());

            // 指纹变化：ToolChanged 且记录失效。
            var mismatch = await InvokeAsync("verification.report", new
            {
                idempotencyKey = "v-mismatch",
                recordId,
                gameStarted = true,
                translationConfirmed = true,
                fingerprint = "deadbeef",
            });
            Assert.False(mismatch.Ok);
            Assert.Equal(ErrorCodes.ToolChanged, mismatch.Error!.Code);

            // invalidate → Unknown。
            var invalidate = await InvokeAsync("verification.invalidate", new
            {
                idempotencyKey = $"vinvalidate-{recordId}",
                recordId,
            });
            Assert.True(invalidate.Ok, invalidate.Error?.Message);
            Assert.Equal("unknown", invalidate.Data.GetProperty("status").GetString());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task VerificationLifecycle_FullDualConclusion_ReachesVerifiedAutomatic()
    {
        var root = CreateFakeInstall("rt-verify2");
        try
        {
            var fingerprint = new RenpyThiefAdapter().Discover(root).Fingerprint!;
            var start = await InvokeAsync("verification.start", new
            {
                idempotencyKey = "v2-start",
                toolId = "renpythief",
                fingerprint,
                engine = "unity",
                samplePath = @"D:\Official\GameLibrary\artifacts\test-runs\sandbox2",
            });
            var recordId = start.Data.GetProperty("recordId").GetString()!;

            var report = await InvokeAsync("verification.report", new
            {
                idempotencyKey = "v2-both",
                recordId,
                gameStarted = true,
                translationConfirmed = true,
            });
            Assert.True(report.Ok, report.Error?.Message);
            Assert.Equal("verifiedAutomatic", report.Data.GetProperty("status").GetString());
            Assert.True(report.Data.GetProperty("gameStartedConfirmed").GetBoolean());

            var list = await InvokeAsync("verification.list", new { toolId = "renpythief" });
            Assert.Contains(list.Data.GetProperty("items").EnumerateArray(),
                r => r.GetProperty("recordId").GetString() == recordId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
