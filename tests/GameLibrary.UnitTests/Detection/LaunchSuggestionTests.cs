using GameLibrary.Domain.Detection;
using Xunit;

namespace GameLibrary.UnitTests.Detection;

public sealed class LaunchSuggestionTests
{
    [Fact]
    public void ChinesePairOutranksOriginalAndHelpersNeverWin()
    {
        var snapshot = new InMemorySnapshot().File("advhd.exe").File("advhd_chs.exe")
            .File("settings.exe").File("inst.exe").File("uninst.exe").File("UnityCrashHandler64.exe");
        var entries = LaunchSuggestionDetector.Detect(snapshot);
        Assert.Equal(2, entries.Count);
        Assert.Equal("advhd_chs.exe", entries[0].RelativePath);
        Assert.True(EntryScoring.MeetsPrescore(entries[0], entries[1]));
    }

    [Fact]
    public void AmbiguousChineseEntriesRequireSelection()
    {
        var entries = LaunchSuggestionDetector.Detect(new InMemorySnapshot().File("game.exe")
            .File("game_chs.exe").File("game_cn.exe"));
        Assert.False(EntryScoring.MeetsPrescore(entries[0], entries[1]));
    }

    [Fact]
    public void SingleEntryAndUnityPairAreRecommendedButMultipleGenericEntriesAreNot()
    {
        var single = LaunchSuggestionDetector.Detect(new InMemorySnapshot().File("stop.exe").File("settings.exe"));
        Assert.True(EntryScoring.MeetsPrescore(Assert.Single(single), null));
        var unity = LaunchSuggestionDetector.Detect(new InMemorySnapshot().File("foo.exe").File("foo_chs.exe")
            .File("UnityPlayer.dll").Directory("foo_Data"));
        Assert.Equal("foo_chs.exe", unity[0].RelativePath);
        Assert.True(EntryScoring.MeetsPrescore(unity[0], unity[1]));
        var generic = LaunchSuggestionDetector.Detect(new InMemorySnapshot().File("one.exe").File("two.exe"));
        Assert.False(EntryScoring.MeetsPrescore(generic[0], generic[1]));
    }

    [Fact]
    public void TooManyEntriesDoNotProduceIncompleteRecommendations()
    {
        var snapshot = new InMemorySnapshot();
        for (var i = 0; i < 257; i++) snapshot.File($"file{i}.exe");
        Assert.Empty(LaunchSuggestionDetector.Detect(snapshot));
    }

    [Theory]
    [InlineData("bin.exe", "TrueFacials.exe")]
    [InlineData("app.exe", "launcher.exe")]
    public void GenericInnerBinaryDoesNotBypassLauncher(string binary, string launcher)
    {
        var snapshot = new InMemorySnapshot().File(binary).File(launcher).File("UnityPlayer.dll")
            .Directory(Path.GetFileNameWithoutExtension(binary) + "_Data");
        var entries = LaunchSuggestionDetector.Detect(snapshot);
        Assert.False(EntryScoring.MeetsPrescore(entries[0], entries[1]));
    }
}
