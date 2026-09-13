using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using Xunit;

namespace GameLibrary.UnitTests.Detection;

public sealed class DetectorTests
{
    private static readonly IEngineDetector[] AllDetectors =
    [
        new UnityDetector(),
        new RpgMakerMvMzDetector(),
        new RenpyDetector(),
        new KirikiriDetector(),
        new FlashDetector(),
    ];

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Unity_PairedDataWithPlayer_IsHighWithPairedEntry()
    {
        var snapshot = new InMemorySnapshot()
            .Directory("ExampleC_Data")
            .File("ExampleC_Data/globalgamemanagers")
            .File("UnityPlayer.dll")
            .File("ExampleC.exe")
            .File("UnityCrashHandler64.exe");

        var result = new UnityDetector().Detect(snapshot);

        Assert.NotNull(result);
        Assert.Equal(DetectionConfidence.High, result!.Confidence);
        var top = Assert.Single(result.EntryCandidates);
        Assert.Equal("ExampleC.exe", top.RelativePath);
        Assert.True(top.Score >= EntryCandidate.PrescoreThreshold, $"分数 {top.Score}");
        Assert.DoesNotContain(result.Evidence, e => e.RelativePath == "UnityCrashHandler64.exe" && e.Polarity == EvidencePolarity.Positive);
    }

    [Fact]
    public void Unity_PlayerOnly_IsMediumNotHigh()
    {
        var snapshot = new InMemorySnapshot().File("UnityPlayer.dll");

        var result = new UnityDetector().Detect(snapshot);

        Assert.NotNull(result);
        Assert.Equal(DetectionConfidence.Medium, result!.Confidence);
    }

    [Fact]
    public void RpgMakerMv_CoreAndSystemJson_IsHigh_CoreOnlyIsMedium()
    {
        var full = new InMemorySnapshot()
            .Directory("www").Directory("www/js").Directory("www/data")
            .File("www/js/rpg_core.js")
            .File("www/data/System.json")
            .File("Game.exe");
        var coreOnly = new InMemorySnapshot()
            .Directory("js")
            .File("js/rpg_core.js");

        var fullResult = new RpgMakerMvMzDetector().Detect(full);
        var coreOnlyResult = new RpgMakerMvMzDetector().Detect(coreOnly);

        Assert.Equal(DetectionConfidence.High, fullResult!.Confidence);
        Assert.Equal(DetectionConfidence.Medium, coreOnlyResult!.Confidence);
        Assert.Contains(fullResult.EntryCandidates, c => c.RelativePath == "Game.exe");
    }

    [Fact]
    public void Renpy_PlaintextRpyWithoutRpa_IsHigh()
    {
        var snapshot = new InMemorySnapshot()
            .Directory("renpy")
            .Directory("game")
            .File("game/options.rpy")
            .File("Example.exe")
            .File("renpy/renpy.exe");

        var result = new RenpyDetector().Detect(snapshot);

        Assert.Equal(DetectionConfidence.High, result!.Confidence);
        var candidate = Assert.Single(result.EntryCandidates);
        Assert.Equal("Example.exe", candidate.RelativePath);
    }

    [Fact]
    public void Kirikiri_Xp3WithExeIsHigh_Xp3OnlyIsMedium()
    {
        var withExe = new InMemorySnapshot().File("data.xp3").File("Game.exe");
        var xp3Only = new InMemorySnapshot().File("data.xp3");

        Assert.Equal(DetectionConfidence.High, new KirikiriDetector().Detect(withExe)!.Confidence);
        Assert.Equal(DetectionConfidence.Medium, new KirikiriDetector().Detect(xp3Only)!.Confidence);
    }

    [Theory]
    [InlineData("FWS")]
    [InlineData("CWS")]
    [InlineData("ZWS")]
    public void Flash_ValidHeadersAreHighWithPerFileCandidates(string header)
    {
        var snapshot = new InMemorySnapshot()
            .File("a.swf", Ascii(header))
            .File("b.swf", Ascii(header));

        var result = new FlashDetector().Detect(snapshot);

        Assert.Equal(DetectionConfidence.High, result!.Confidence);
        Assert.Equal(2, result.EntryCandidates.Count);
        Assert.Contains(result.EntryCandidates, c => c.RelativePath == "a.swf");
        Assert.Contains(result.EntryCandidates, c => c.RelativePath == "b.swf");
    }

    [Fact]
    public void Flash_CorruptHeaderIsNegativeEvidenceAndNoCandidate()
    {
        var snapshot = new InMemorySnapshot()
            .File("a.swf", Ascii("BAD"))
            .File("b.swf", Ascii("FWS"));

        var result = new FlashDetector().Detect(snapshot);

        Assert.Equal(DetectionConfidence.High, result!.Confidence);
        var candidate = Assert.Single(result.EntryCandidates);
        Assert.Equal("b.swf", candidate.RelativePath);
        Assert.Contains(result.Evidence, e => e.RelativePath == "a.swf" && e.Polarity == EvidencePolarity.Negative);
    }

    [Fact]
    public void Flash_UnreadableHeader_IsContextualUnreadableNotNegative()
    {
        var snapshot = new InMemorySnapshot().File("locked.swf", unreadable: true);

        var result = new FlashDetector().Detect(snapshot);

        var evidence = Assert.Single(result!.Evidence);
        Assert.Equal(EvidenceObservation.Unreadable, evidence.Observation);
        Assert.Equal(EvidencePolarity.Contextual, evidence.Polarity);
        Assert.Empty(result.EntryCandidates);
        Assert.Equal(DetectionConfidence.Low, result.Confidence);
    }

    [Fact]
    public void EmptyDirectory_NoDetectorProducesResult()
    {
        var report = new EngineDetectorSet(AllDetectors).DetectAll(new InMemorySnapshot());

        Assert.Empty(report.Results);
        Assert.Null(report.Conflict);
    }

    [Fact]
    public void TwoHighEngines_ProduceConflictNotFirstRegistered()
    {
        // Unity high + RPG MV high 共存（矛盾结构，FS-09 形态）。
        var snapshot = new InMemorySnapshot()
            .Directory("Game_Data").File("Game_Data/globalgamemanagers").File("UnityPlayer.dll").File("Game.exe")
            .Directory("js").File("js/rpg_core.js")
            .Directory("data").File("data/System.json");

        var report = new EngineDetectorSet(AllDetectors).DetectAll(snapshot);

        Assert.NotNull(report.Conflict);
        Assert.Equal(2, report.Conflict!.ConflictingResults.Count);
        Assert.Contains(report.Conflict.ConflictingResults, r => r.Engine == EngineId.Unity);
        Assert.Contains(report.Conflict.ConflictingResults, r => r.Engine == EngineId.RpgMakerMvMz);
    }

    [Fact]
    public void Report_IsIndependentOfDetectorRegistrationOrder()
    {
        var snapshot = new InMemorySnapshot()
            .File("data.xp3").File("Game.exe");

        var forward = new EngineDetectorSet(AllDetectors).DetectAll(snapshot);
        var backward = new EngineDetectorSet(AllDetectors.Reverse()).DetectAll(snapshot);

        Assert.Equal(forward.Results.Count, backward.Results.Count);
        for (var i = 0; i < forward.Results.Count; i++)
        {
            Assert.Equal(forward.Results[i].Engine, backward.Results[i].Engine);
            Assert.Equal(forward.Results[i].Confidence, backward.Results[i].Confidence);
        }
    }

    [Fact]
    public void UninstallersAndSetups_NeverBecomeEntryCandidates()
    {
        var snapshot = new InMemorySnapshot()
            .File("data.xp3")
            .File("Game.exe")
            .File("unins000.exe")
            .File("Setup.exe");

        var result = new KirikiriDetector().Detect(snapshot);

        var candidate = Assert.Single(result!.EntryCandidates);
        Assert.Equal("Game.exe", candidate.RelativePath);
    }
}
