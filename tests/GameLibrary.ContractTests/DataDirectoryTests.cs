using GameLibrary.Contracts.Ipc;
using Xunit;

namespace GameLibrary.ContractTests;

public sealed class DataDirectoryTests
{
    [Theory]
    [InlineData(@"D:\Official\GameLibrary\LocalData", @"D:\OFFICIAL\GAMELIBRARY\LOCALDATA")]
    [InlineData(@"d:/official/gameLibrary/localData/", @"D:\OFFICIAL\GAMELIBRARY\LOCALDATA")]
    [InlineData(@"D:\a\b\..\c\.\d", @"D:\A\C\D")]
    [InlineData(@"F:\", @"F:\")]
    public void Resolve_ProducesCanonicalAndComparisonForms(string input, string expectedKey)
    {
        var result = DataDirectory.Resolve(input);

        Assert.True(result.IsValid);
        Assert.Equal(expectedKey, result.ComparisonKey);
    }

    [Theory]
    [InlineData("", "DataDirectoryEmpty")]
    [InlineData("   ", "DataDirectoryEmpty")]
    [InlineData(@"\\server\share\data", "DataDirectoryUnc")]
    [InlineData(@"\\?\D:\data", "DataDirectoryDeviceNamespace")]
    [InlineData(@"..\relative", "DataDirectoryNotAbsoluteLocal")]
    [InlineData(@"D:no-separator", "DataDirectoryNotAbsoluteLocal")]
    [InlineData(@"D:\..\data", "DataDirectoryEscapesRoot")]
    [InlineData(@"D:\da""ta", "DataDirectoryIllegalCharacters")]
    [InlineData(@"D:\data:stream", "DataDirectoryIllegalColon")]
    [InlineData(@"D:\data\trailing.", "DataDirectoryTrailingDotOrSpace")]
    public void Resolve_RejectsInvalidInputsWithExplicitErrors(string input, string expectedError)
    {
        var result = DataDirectory.Resolve(input);

        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void ChannelNames_AreStableAndDerivedFromComparisonKey()
    {
        var a = DataDirectory.Resolve(@"D:\Official\GameLibrary\LocalData");
        var alias = DataDirectory.Resolve(@"d:/official/gamelibrary/localdata/");

        Assert.Equal(ChannelNames.PipeName(a.ComparisonKey!), ChannelNames.PipeName(alias.ComparisonKey!));
        Assert.Equal(ChannelNames.MutexName(a.ComparisonKey!), ChannelNames.MutexName(alias.ComparisonKey!));

        var other = DataDirectory.Resolve(@"D:\Other");
        Assert.NotEqual(ChannelNames.PipeName(a.ComparisonKey!), ChannelNames.PipeName(other.ComparisonKey!));
        Assert.StartsWith("GameLibrary.", ChannelNames.PipeName(a.ComparisonKey!));
    }

    [Fact]
    public void Resolve_SegmentedPrefixIsNotConfusedByCommonPrefix()
    {
        var lib = DataDirectory.Resolve(@"D:\Data\lib");
        var lib2 = DataDirectory.Resolve(@"D:\Data\lib2");

        Assert.NotEqual(lib.ComparisonKey, lib2.ComparisonKey);
        Assert.NotEqual(ChannelNames.PipeName(lib.ComparisonKey!), ChannelNames.PipeName(lib2.ComparisonKey!));
    }
}
