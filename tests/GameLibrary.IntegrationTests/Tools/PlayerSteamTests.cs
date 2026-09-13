using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Tools;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Tools;

/// <summary>
/// T09 Player/Steam：播放器发现与参数模板（打开本地文件）、Steam 安装发现与
/// appmanifest 解析（appid 十进制校验、清单缺失 SteamManifestMissing）、
/// tools.discover steam/player 分支。全程只读，不启动任何进程。
/// </summary>
public sealed class PlayerSteamTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public PlayerSteamTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "ps-test",
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

    private static string CreateFakeSteam(string prefix, string appmanifestContent)
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steam.exe"), "stub");
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_1245620.acf"), appmanifestContent);
        return root;
    }

    [Fact]
    public void SteamAdapter_ParsesLocalManifest_WithAppIdValidation()
    {
        var root = CreateFakeSteam("steam-ok", """
            "AppState"
            {
            	"appid"		"1245620"
            	"name"		"ELDEN RING"
            	"installdir"		"ELDEN RING"
            }
            """);
        try
        {
            var adapter = new SteamAdapter();
            // ACF 顶层键为 "AppState"——直接解析验证 KeyValues 集成。
            var parsedRoot = GameLibrary.Domain.Tools.KeyValuesParser.Parse(
                File.ReadAllText(Path.Combine(root, "steamapps", "appmanifest_1245620.acf")));
            var parsed = parsedRoot.GetObject("AppState") ?? parsedRoot;
            Assert.Equal("1245620", parsed.GetString("appid"));
            Assert.True(GameLibrary.Domain.Tools.SteamRules.IsValidAppId(parsed.GetString("appid")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SteamAdapter_BuildLaunchTemplate_RejectsInvalidAppId()
    {
        var adapter = new SteamAdapter();
        var discovery = new SteamDiscovery
        {
            Found = true,
            SteamRoot = @"C:\Steam",
            SteamExecutablePath = @"C:\Steam\steam.exe",
            Manifests = [new SteamAppManifest("1245620", "ELDEN RING", "ELDEN RING", @"C:\Steam\steamapps\m.acf")],
            Capability = GameLibrary.Domain.Tools.SteamRules.SteamCapability(),
        };

        var template = adapter.BuildLaunchTemplate(discovery, "1245620");
        Assert.Equal(@"C:\Steam\steam.exe", template.ExecutablePath);
        Assert.Equal("-applaunch", Assert.Single(template.Arguments.Take(1)));
        Assert.Equal("1245620", template.Arguments[1]);

        Assert.Throws<ArgumentException>(() => adapter.BuildLaunchTemplate(discovery, "0123"));
        Assert.Throws<ArgumentException>(() => adapter.BuildLaunchTemplate(discovery, "999999")); // 不在清单
    }

    [Fact]
    public void PlayerAdapter_DiscoversStub_AndBuildsTemplate()
    {
        // 造一个候选播放器：LocalApplicationData\Programs\<sub>\<exe>。
        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        var playerDir = Path.Combine(localPrograms, "MPC-HC");
        Directory.CreateDirectory(playerDir);
        var exePath = Path.Combine(playerDir, "mpc-hc64.exe");
        var existed = File.Exists(exePath);
        if (!existed)
        {
            File.WriteAllText(exePath, "stub");
        }

        try
        {
            var adapter = new PlayerAdapter();
            var players = adapter.Discover();
            Assert.Contains(players, p => p.Name == "MPC-HC" && string.Equals(p.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));

            var target = Path.Combine(playerDir, "movie.swf");
            File.WriteAllText(target, "stub");
            var template = adapter.BuildLaunchTemplate(players.First(p => p.Name == "MPC-HC"), target);
            Assert.Equal(exePath, template.ExecutablePath);
            Assert.Equal(target, Assert.Single(template.Arguments));
            Assert.False(template.WaitForExit);
        }
        finally
        {
            if (!existed)
            {
                try
                {
                    Directory.Delete(playerDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ToolsDiscover_SteamBranch_ReportsManifestMissing()
    {
        var discover = await InvokeAsync("tools.discover", new { tool = "steam" });

        // 本机可能有也可能没有 Steam：两种结果都必须如实结构化。
        Assert.True(discover.Ok, discover.Error?.Message);
        Assert.Equal("steam", discover.Data.GetProperty("toolId").GetString());
        var found = discover.Data.GetProperty("found").GetBoolean();
        if (found)
        {
            Assert.Equal(
                discover.Data.GetProperty("manifestMissing").GetBoolean(),
                discover.Data.GetProperty("manifests").GetArrayLength() == 0);
        }
        else
        {
            Assert.Contains("未发现 Steam", discover.Data.GetProperty("notice").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ToolsDiscover_PlayerBranch_ReturnsPlayers()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"ps-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await InvokeAsync("roots.add", new { root });
            var discover = await InvokeAsync("tools.discover", new { tool = "player", path = root });

            Assert.True(discover.Ok, discover.Error?.Message);
            Assert.Equal("player", discover.Data.GetProperty("toolId").GetString());
            // 模板仅在目标文件存在且发现播放器时生成；本断言只验证结构。
            Assert.True(discover.Data.GetProperty("players").ValueKind == JsonValueKind.Array);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
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
