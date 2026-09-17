using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>
/// T30 统一验收：无 GUI 全链路（AI-02）——agent 仅凭 CLI 完成
/// init → scan → accept → fields.set → profiles.create(stub) → launch.execute → backups.create。
/// 全部走真实宿主进程与命名管道，无截图点击、无直改 SQLite。
/// </summary>
public sealed class FullWorkflowE2ETests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "gamelibrary.exe");

    private static string NewRunDir() =>
        Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"t30-{Guid.NewGuid():N}");

    [Fact]
    public async Task Cli_SettingsPatch_OnlySendsSpecifiedFontFields()
    {
        var runDir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(runDir, "data");
        try
        {
            Assert.Equal(0, (await RunCliAsync("library", "init", "--data-dir", dataDir)).ExitCode);
            var settings = await RunCliAsync("settings", "get", "--data-dir", dataDir);
            Assert.Equal(0, settings.ExitCode);
            using var baseline = Json(settings.StdOut);
            var revision = baseline.RootElement.GetProperty("data")
                .GetProperty("revision").GetInt32();

            var font = await RunCliAsync("settings", "update", "--data-dir", dataDir,
                "--expected-revision", revision.ToString(),
                "--font-family", "Microsoft YaHei UI");
            Assert.Equal(0, font.ExitCode);
            using var fontResult = Json(font.StdOut);
            Assert.Equal("Microsoft YaHei UI", fontResult.RootElement.GetProperty("data")
                .GetProperty("uiFontFamily").GetString());

            var scale = await RunCliAsync("settings", "update", "--data-dir", dataDir,
                "--expected-revision", (revision + 1).ToString(), "--font-scale", "1.3");
            Assert.Equal(0, scale.ExitCode);
            using var scaleResult = Json(scale.StdOut);
            Assert.Equal(1.3, scaleResult.RootElement.GetProperty("data")
                .GetProperty("uiFontScale").GetDouble());
            Assert.Equal("Microsoft YaHei UI", scaleResult.RootElement.GetProperty("data")
                .GetProperty("uiFontFamily").GetString());
        }
        finally
        {
            await StopOwnHostAsync(dataDir);
            TryCleanup(runDir);
        }
    }

    [Fact]
    public async Task Cli_ManualGame_CreateAndRemove_UsesRealHostAndPreservesFile()
    {
        var runDir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(runDir, "data");
        var gameRoot = Path.Combine(dataDir, "games");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "Manual Game.exe");
        File.WriteAllText(exe, "test-stub");

        try
        {
            Assert.Equal(0, (await RunCliAsync("library", "init", "--data-dir", dataDir)).ExitCode);
            Assert.Equal(0, (await RunCliAsync("roots", "add", "--root", gameRoot,
                "--data-dir", dataDir)).ExitCode);

            var created = await RunCliAsync("games", "create", "--source-path", exe,
                "--name", "手动游戏", "--data-dir", dataDir);
            Assert.Equal(0, created.ExitCode);
            using var createdJson = Json(created.StdOut);
            var card = createdJson.RootElement.GetProperty("data");
            var gameId = card.GetProperty("gameId").GetString()!;
            Assert.Equal("手动游戏", card.GetProperty("title").GetString());
            Assert.Equal("manualFile", card.GetProperty("kind").GetString());

            var removed = await RunCliAsync("games", "remove", "--game-id", gameId,
                "--expected-revision", card.GetProperty("revision").GetInt32().ToString(),
                "--data-dir", dataDir);
            Assert.Equal(0, removed.ExitCode);
            using var removedJson = Json(removed.StdOut);
            Assert.False(removedJson.RootElement.GetProperty("data")
                .GetProperty("filesDeleted").GetBoolean());
            Assert.True(File.Exists(exe));

            var games = await RunCliAsync("games", "list", "--data-dir", dataDir);
            Assert.Equal(0, games.ExitCode);
            using var gamesJson = Json(games.StdOut);
            Assert.Equal(0, gamesJson.RootElement.GetProperty("data").GetProperty("total").GetInt32());
        }
        finally
        {
            await StopOwnHostAsync(dataDir);
            TryCleanup(runDir);
        }
    }

    [Fact]
    public async Task Cli_FullWorkflow_NoGui_FromInitToBackup()
    {
        var runDir = NewRunDir();
        var dataDir = Path.Combine(runDir, "data");
        var scanRoot = Path.Combine(runDir, "tree");
        var gameDir = Path.Combine(scanRoot, "MyGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "Game.exe"), "stub");
        File.WriteAllText(Path.Combine(gameDir, "data.xp3"), "stub");

        try
        {
            // 1. 建库。
            var init = await RunCliAsync("library", "init", "--data-dir", dataDir, "--idempotency-key", $"t30-{Guid.NewGuid():N}");
            Assert.Equal(0, init.ExitCode);

            // 2. 注册扫描根（v1 审查修复：scan.start 要求路径在已注册库根内，缺 roots.add 必失败）。
            var addRoot = await RunCliAsync("roots", "add", "--root", scanRoot, "--data-dir", dataDir);
            Assert.Equal(0, addRoot.ExitCode);

            // 3. 扫描两轮（等作业完成；复扫把候选从 observed 晋升到 pendingReview，与候选编排一致）。
            string jobId = "";
            for (var round = 0; round < 2; round++)
            {
                var scan = await RunCliAsync("scan", "start", "--root", scanRoot, "--data-dir", dataDir);
                Assert.Equal(0, scan.ExitCode);
                jobId = JsonDocument.Parse(scan.StdOut).RootElement.GetProperty("jobId").GetString()!;
                await WaitForJobAsync(dataDir, jobId, "succeeded");
            }

            // 3. 候选审核：accept 第一个候选。
            var candidates = await RunCliAsync("candidates", "list", "--data-dir", dataDir);
            Assert.Equal(0, candidates.ExitCode);
            var candidatesData = JsonDocument.Parse(candidates.StdOut).RootElement.GetProperty("data");
            Assert.True(candidatesData.TryGetProperty("items", out var candidateItems)
                    && candidateItems.GetArrayLength() > 0,
                $"扫描后无候选：{candidates.StdOut}");
            var first = candidateItems[0];
            var candidateId = first.GetProperty("candidateId").GetString()!;
            var revision = first.GetProperty("revision").GetInt32();
            var accept = await RunCliAsync("candidates", "accept",
                "--candidate-id", candidateId, "--expected-revision", revision.ToString(),
                "--idempotency-key", $"t30-accept-{Guid.NewGuid():N}", "--data-dir", dataDir);
            Assert.Equal(0, accept.ExitCode);
            var gameId = JsonDocument.Parse(accept.StdOut).RootElement.GetProperty("data").GetProperty("gameId").GetString()!;

            // 4. 资料编辑。
            var fieldSet = await RunCliAsync("fields", "set", "--game-id", gameId, "--field", "title",
                "--value", "验收标题", "--expected-revision", "1",
                "--idempotency-key", $"t30-{Guid.NewGuid():N}", "--data-dir", dataDir);
            Assert.Equal(0, fieldSet.ExitCode);

            // 4b. 翻译策略显式 NotRequired（KR 引擎继承 Required，直启 Profile 无工具绑定会被拒绝——不回退直启）。
            var translation = await RunCliAsync("translation", "set", "--game-id", gameId,
                "--override", "NotRequired", "--expected-revision", "2",
                "--idempotency-key", $"t30-trans-{Guid.NewGuid():N}", "--data-dir", dataDir);
            Assert.Equal(0, translation.ExitCode);

            // 5. 启动配置（TestProcessStub 当作游戏入口）+ 执行。
            // 测试 bin 目录注册为库根（LaunchE2ETests 同款模式）：Profile exe 必须落在已注册根内。
            var addBinRoot = await RunCliAsync("roots", "add", "--root", AppContext.BaseDirectory, "--data-dir", dataDir);
            Assert.Equal(0, addBinRoot.ExitCode);
            var stub = Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");
            var create = await RunCliAsync("profiles", "create", "--game-id", gameId, "--exe", stub,
                "--arg", "--e2e", "--cwd", AppContext.BaseDirectory,
                "--idempotency-key", $"t30-{Guid.NewGuid():N}", "--data-dir", dataDir);
            Assert.Equal(0, create.ExitCode);
            var profileId = JsonDocument.Parse(create.StdOut).RootElement.GetProperty("data").GetProperty("profileId").GetString()!;
            var launch = await RunCliAsync("launch", "execute", "--profile-id", profileId,
                "--idempotency-key", $"t30-launch-{Guid.NewGuid():N}", "--data-dir", dataDir);
            Assert.Equal(0, launch.ExitCode);

            // 6. 备份。
            var backup = await RunCliAsync("backups", "create", "--data-dir", dataDir,
                "--idempotency-key", $"t30-bk-{Guid.NewGuid():N}");
            Assert.Equal(0, backup.ExitCode);
            var backupJobId = JsonDocument.Parse(backup.StdOut).RootElement.GetProperty("jobId").GetString()!;
            await WaitForJobAsync(dataDir, backupJobId, "succeeded");

            // 7. 导出诊断（最终确认三入口数据面一致）。
            var diag = await RunCliAsync("diagnostics", "status", "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, diag.ExitCode);
        }
        finally
        {
            // v1 审查修复：不再按进程名杀死机器上所有 Host/MCP（会波及并行 E2E），
            // 只通过本测试自己的数据目录优雅停机（host.stop 排空后宿主自行退出）。
            await StopOwnHostAsync(dataDir);
            TryCleanup(runDir);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CliExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static JsonDocument Json(string stdout) => JsonDocument.Parse(stdout);

    private static async Task WaitForJobAsync(string dataDir, string jobId, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = await RunCliAsync("jobs", "get", "--job-id", jobId, "--data-dir", dataDir, "--format", "json");
            var state = JsonDocument.Parse(status.StdOut).RootElement.GetProperty("data").GetProperty("state").GetString();
            if (state == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"作业 {jobId} 未在 20 秒内进入 {expected}");
    }

    private static async Task StopOwnHostAsync(string dataDir)
    {
        try
        {
            await RunCliAsync("host", "stop", "--data-dir", dataDir);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 宿主可能已经退出。
        }
    }

    private static void TryCleanup(string path)
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
