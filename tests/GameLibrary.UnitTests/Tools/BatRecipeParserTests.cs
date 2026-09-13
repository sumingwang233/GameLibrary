using GameLibrary.Domain.Tools;
using Xunit;

namespace GameLibrary.UnitTests.Tools;

/// <summary>
/// T07 最小 BAT 解析（策划案 9.2/9.3-A.2）：真实结构两步配方、mzHook/nw.exe 变体、
/// %~dp0 展开；变量/CALL/FOR/IF/管道/重定向/非工具 start 一律 Unsupported。
/// </summary>
public sealed class BatRecipeParserTests
{
    private const string ToolRoot = @"X:\TOOLS\MTool\Tool";

    private const string RealStructureScript = """
        @echo off
        chcp 65001>nul
        cd /d "%~dp0"
        "X:\TOOLS\MTool\Tool\loaders\inject.exe" "D:\games\_game\game.exe" "X:\TOOLS\MTool\Tool\loaders\SRPGHook.dll"
        start "" "X:\TOOLS\MTool\Tool\MTool.exe" "X:\TOOLS\MTool\Tool"
        echo 启动完成
        """;

    private static BatRecipeParser.ParseResult Parse(string script, string scriptDir = @"D:\games\_game") =>
        BatRecipeParser.Parse(scriptDir, script);

    [Fact]
    public void RealStructureScript_ParsesTwoTypedSteps()
    {
        var result = Parse(RealStructureScript);

        Assert.True(result.IsSupported, result.UnsupportedReason);
        var recipe = result.Recipe!;
        Assert.Equal(2, recipe.Steps.Count);

        var inject = recipe.Steps[0];
        Assert.Equal(@"X:\TOOLS\MTool\Tool\loaders\inject.exe", inject.ExecutablePath);
        Assert.Equal(@"D:\games\_game\game.exe", Assert.Single(inject.Arguments.Take(1)));
        Assert.Equal(@"X:\TOOLS\MTool\Tool\loaders\SRPGHook.dll", inject.Arguments[1]);
        Assert.True(inject.WaitForExit);
        Assert.Equal(@"D:\games\_game", inject.WorkingDirectory);

        var tool = recipe.Steps[1];
        Assert.Equal(@"X:\TOOLS\MTool\Tool\MTool.exe", tool.ExecutablePath);
        Assert.Equal(@"X:\TOOLS\MTool\Tool", tool.Arguments[0]);
        Assert.False(tool.WaitForExit);

        Assert.Contains(@"X:\TOOLS\MTool\Tool\loaders\SRPGHook.dll", recipe.ReferencedFiles);
        Assert.Equal(64, recipe.ScriptSha256.Length); // SHA-256 hex
    }

    [Fact]
    public void MzHookAndNwExeVariant_Parses()
    {
        var script = $"""
            cd /d "%~dp0"
            "{ToolRoot}\loaders\inject.exe" "%~dp0game.exe" "{ToolRoot}\loaders\mzHook.dll"
            start "" "{ToolRoot}\nw.exe" "{ToolRoot}"
            """;

        var result = Parse(script);

        Assert.True(result.IsSupported, result.UnsupportedReason);
        Assert.EndsWith("nw.exe", result.Recipe!.Steps[1].ExecutablePath, StringComparison.OrdinalIgnoreCase);
        // %~dp0 展开为脚本目录（不带尾随分隔符的工作目录）。
        Assert.StartsWith(@"D:\games\_game", result.Recipe.Steps[0].Arguments[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("set FOO=1\r\necho done")]
    [InlineData("call other.bat\r\necho done")]
    [InlineData("for %%f in (*.dll) do echo %%f\r\necho done")]
    [InlineData("if exist x.exe echo found\r\necho done")]
    [InlineData("\"X:\\inject.exe\" \"D:\\g.exe\" \"X:\\h.dll\" | findstr ok")]
    [InlineData("echo log > run.log\r\necho done")]
    [InlineData("start \"\" \"X:\\something.exe\" \"X:\\dir\"\r\necho done")]
    [InlineData("\"X:\\other\\tool.exe\" \"X:\\dir\"\r\necho done")]
    public void OutOfScopeSyntax_IsUnsupportedWithoutRecipe(string script)
    {
        var result = Parse(script);

        Assert.False(result.IsSupported);
        Assert.Null(result.Recipe);
        Assert.False(string.IsNullOrWhiteSpace(result.UnsupportedReason));
    }

    [Fact]
    public void StartWithNonToolExecutable_IsUnsupported()
    {
        var result = Parse($"start \"\" \"X:\\helper.exe\" \"X:\\dir\"");

        Assert.False(result.IsSupported);
        Assert.Contains("start", result.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyScript_IsUnsupported()
    {
        var result = Parse("echo nothing here");

        Assert.False(result.IsSupported);
        Assert.Contains("没有可识别", result.UnsupportedReason);
    }

    [Fact]
    public void Capability_DeclaresSideEffectsHonest()
    {
        var capability = MToolCapability.Describe();

        Assert.Equal("Supported", capability["canLaunch"]);
        Assert.Equal("Supported", capability["canRequestInjection"]);
        Assert.Equal("Unsupported", capability["canDeploy"]);
        Assert.Equal("Unsupported", capability["canRollback"]);
        Assert.Equal("Unknown", capability["mayUseNetwork"]);
        Assert.Equal("mtool", MToolCapability.ToolId);
    }
}
