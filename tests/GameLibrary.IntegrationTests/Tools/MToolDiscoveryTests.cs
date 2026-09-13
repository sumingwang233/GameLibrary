using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Tools;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Tools;

/// <summary>
/// T07 MTool 适配（经真实管道）：生成配方发现/断链/Unsupported 如实返回、
/// 旧路径重映射后验证归零语义。只读，不自启动任何工具。
/// </summary>
public sealed class MToolDiscoveryTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public MToolDiscoveryTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "tool-test",
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

    private static string NewRoot(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task ToolsDiscover_ValidRecipe_ReturnsGeneratedWithCapability()
    {
        var root = NewRoot("mtool-ok");
        var toolRoot = NewRoot("mtool-tool");
        Directory.CreateDirectory(Path.Combine(toolRoot, "loaders"));
        File.WriteAllText(Path.Combine(toolRoot, "loaders", "inject.exe"), "stub");
        File.WriteAllText(Path.Combine(toolRoot, "loaders", "SRPGHook.dll"), "stub");
        File.WriteAllText(Path.Combine(toolRoot, "MTool.exe"), "stub");
        var gameExe = Path.Combine(root, "game.exe");
        File.WriteAllText(gameExe, "stub");
        File.WriteAllText(Path.Combine(root, "与工具一同启动.bat"), $"""
            @echo off
            chcp 65001>nul
            cd /d "%~dp0"
            "{Path.Combine(toolRoot, "loaders", "inject.exe")}" "{gameExe}" "{Path.Combine(toolRoot, "loaders", "SRPGHook.dll")}"
            start "" "{Path.Combine(toolRoot, "MTool.exe")}" "{toolRoot}"
            echo done
            """);

        await InvokeAsync("roots.add", new { root });
        var discover = await InvokeAsync("tools.discover", new { path = root });

        Assert.True(discover.Ok, discover.Error?.Message);
        Assert.Equal("Generated", discover.Data.GetProperty("evidenceKind").GetString());
        Assert.Equal("Unsupported", discover.Data.GetProperty("capability").GetProperty("canDeploy").GetString());
        var recipe = discover.Data.GetProperty("recipe");
        Assert.False(recipe.GetProperty("isBroken").GetBoolean());
        Assert.Equal(2, recipe.GetProperty("steps").GetArrayLength());
        Assert.True(recipe.GetProperty("steps")[0].GetProperty("waitForExit").GetBoolean());
        Assert.Contains("运行验证", discover.Data.GetProperty("notice").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsDiscover_OldDrivePaths_ReportsBrokenRecipe()
    {
        var root = NewRoot("mtool-broken");
        File.WriteAllText(Path.Combine(root, "game.exe"), "stub");
        File.WriteAllText(Path.Combine(root, "与工具一同启动.bat"), $"""
            cd /d "%~dp0"
            "E:\OldTools\MTool\Tool\loaders\inject.exe" "{Path.Combine(root, "game.exe")}" "E:\OldTools\MTool\Tool\loaders\mzHook.dll"
            start "" "E:\OldTools\MTool\Tool\nw.exe" "E:\OldTools\MTool\Tool"
            echo done
            """);

        await InvokeAsync("roots.add", new { root });
        var discover = await InvokeAsync("tools.discover", new { path = root });

        Assert.True(discover.Ok, discover.Error?.Message);
        var recipe = discover.Data.GetProperty("recipe");
        Assert.True(recipe.GetProperty("isBroken").GetBoolean());
        Assert.True(recipe.GetProperty("brokenPaths").GetArrayLength() >= 3);
        Assert.Contains("BrokenRecipe", discover.Data.GetProperty("notice").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsDiscover_UnsupportedSyntax_ReportsReasonWithoutRecipe()
    {
        var root = NewRoot("mtool-unsup");
        File.WriteAllText(Path.Combine(root, "与工具一同启动.bat"), "set MTOOL_HOME=X:\\mtool\r\necho done\r\n");

        await InvokeAsync("roots.add", new { root });
        var discover = await InvokeAsync("tools.discover", new { path = root });

        Assert.True(discover.Ok, discover.Error?.Message);
        Assert.Equal(JsonValueKind.Null, discover.Data.GetProperty("recipe").ValueKind);
        Assert.Contains("set", discover.Data.GetProperty("unsupportedReason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolsDiscover_OutsideRegisteredRoots_IsDenied()
    {
        var denied = await InvokeAsync("tools.discover", new { path = @"D:\Official\GameLibrary\artifacts" });
        Assert.False(denied.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, denied.Error!.Code);
    }

    [Fact]
    public void RemapToToolInstallation_RewiresBrokenPaths_AndKeepsRevalidationRequirement()
    {
        var adapter = new MToolAdapter();
        var recipe = new GameLibrary.Domain.Tools.BatRecipeParser.ParseResult
        {
            Recipe = new GameLibrary.Domain.Tools.MToolRecipe
            {
                SourcePath = @"D:\games\g\与工具一同启动.bat",
                ScriptSha256 = "abc",
                Steps =
                [
                    new GameLibrary.Domain.Tools.RecipeProcessStep
                    {
                        Sequence = 0,
                        ExecutablePath = @"E:\OldTools\MTool\Tool\loaders\inject.exe",
                        Arguments = [@"D:\games\g\game.exe", @"E:\OldTools\MTool\Tool\loaders\SRPGHook.dll"],
                        WorkingDirectory = @"D:\games\g",
                        WaitForExit = true,
                    },
                ],
                ReferencedFiles =
                [
                    @"E:\OldTools\MTool\Tool\loaders\inject.exe",
                    @"E:\OldTools\MTool\Tool\loaders\SRPGHook.dll",
                ],
                BrokenPaths =
                [
                    @"E:\OldTools\MTool\Tool\loaders\inject.exe",
                    @"E:\OldTools\MTool\Tool\loaders\SRPGHook.dll",
                ],
            },
        }.Recipe!;

        var toolRoot = NewRoot("mtool-remap");
        Directory.CreateDirectory(Path.Combine(toolRoot, "loaders"));
        var injectTarget = Path.Combine(toolRoot, "loaders", "inject.exe");
        File.WriteAllText(injectTarget, "stub");

        var remapped = adapter.RemapToToolInstallation(recipe, toolRoot);

        Assert.Equal(injectTarget, remapped.Steps[0].ExecutablePath);
        Assert.Equal(Path.Combine(toolRoot, "loaders", "SRPGHook.dll"), remapped.Steps[0].Arguments[1]);
        // hook 仍缺失：断链如实保留，验证不能静默通过。
        Assert.Contains(Path.Combine(toolRoot, "loaders", "SRPGHook.dll"), remapped.BrokenPaths);
        Assert.True(remapped.IsBroken);
    }
}
