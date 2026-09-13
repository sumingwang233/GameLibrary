using GameLibrary.Domain.Tools;
using Xunit;

namespace GameLibrary.UnitTests.Tools;

/// <summary>Valve KeyValues 解析器（T09）：嵌套、注释、转义、裸 token、坏格式与大小限制。</summary>
public sealed class KeyValuesParserTests
{
    private const string AcfSample = """
        "AppState"
        {
            "appid"		"1245620"
            "Universe"		"1"
            "name"		"ELDEN RING" // 带注释的名字
            "installdir"		"ELDEN RING"
            "StateFlags"		"4"
            "InstalledByLocalConfig"
            {
                "nested"  "value"
            }
        }
        """;

    [Fact]
    public void Parses_NestedObject_QuotedKeysAndComments()
    {
        var root = KeyValuesParser.Parse(AcfSample);

        // ACF 顶层键为 "AppState"。
        var appState = root.GetObject("AppState");
        Assert.NotNull(appState);
        Assert.Equal("1245620", appState!.GetString("appid"));
        Assert.Equal("ELDEN RING", appState.GetString("name"));
        var nested = appState.GetObject("InstalledByLocalConfig");
        Assert.NotNull(nested);
        Assert.Equal("value", nested!.GetString("nested"));
    }

    [Fact]
    public void Parses_BareTokens_AndEscapes()
    {
        var root = KeyValuesParser.Parse("key1 bare_value\n\"key\\\"2\" \"line\\nn\"");
        Assert.Equal("bare_value", root.GetString("key1"));
        // \" 转义为字面引号；\n 转义为换行。
        Assert.Equal("line\nn", root.GetString("key\"2"));
    }

    [Fact]
    public void BlockComments_AreSkipped()
    {
        var root = KeyValuesParser.Parse("/* block\ncomment */ key value");
        Assert.Equal("value", root.GetString("key"));
    }

    [Fact]
    public void UnclosedQuote_ThrowsFormatException()
    {
        Assert.ThrowsAny<Exception>(() => KeyValuesParser.Parse("key \"unclosed"));
    }

    [Fact]
    public void UnclosedBrace_ThrowsFormatException()
    {
        Assert.ThrowsAny<Exception>(() => KeyValuesParser.Parse("a { b c"));
    }

    [Fact]
    public void OversizedInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeyValuesParser.Parse(new string('x', KeyValuesParser.MaxInputChars + 1)));
    }

    [Fact]
    public void SteamAppId_RequiresDecimalPositive()
    {
        Assert.True(SteamRules.IsValidAppId("1245620"));
        Assert.False(SteamRules.IsValidAppId("0123456")); // 前导零
        Assert.False(SteamRules.IsValidAppId("0"));
        Assert.False(SteamRules.IsValidAppId("12a45"));
        Assert.False(SteamRules.IsValidAppId("-1"));
        Assert.False(SteamRules.IsValidAppId(""));
        Assert.False(SteamRules.IsValidAppId(null));
    }

    [Fact]
    public void Capabilities_DeclareSideEffectsHonest()
    {
        Assert.Equal("Supported", SteamRules.SteamCapability()["canLaunch"]);
        Assert.Equal("Supported", SteamRules.SteamCapability()["mayUseNetwork"]);
        Assert.Equal("Unsupported", PlayerCapability.Describe()["canRequestInjection"]);
        Assert.Equal("player", PlayerCapability.ToolId);
        Assert.Equal("steam", SteamRules.ToolId);
    }
}
