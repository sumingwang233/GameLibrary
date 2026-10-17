using System.Buffers.Binary;
using GameLibrary.Domain.Detection;
using Xunit;

namespace GameLibrary.UnitTests.Detection;

public sealed class AdditionalEngineTests
{
    [Theory]
    [InlineData(EngineId.SrpgStudio, "SRPG Studio Game Engine", "SapphireSoft", null, null)]
    [InlineData(EngineId.PixelGameMakerMv, "Pixel Game Maker MV player", "KADOKAWA CORPORATION", null, null)]
    [InlineData(EngineId.RpgDeveloperBakin, null, null, "RPG Developer Bakin", "bakinplayer_launcher.exe")]
    [InlineData(EngineId.WolfRpg, "WOLF RPG Editor Game", null, null, null)]
    public void PublicVersionResourceEvidence_DetectsEngine_AndDoesNotUseDirectoryOrExeName(EngineId expected, string? description,
        string? company, string? product, string? original)
    {
        var snapshot = new InMemorySnapshot().Identity("Example.exe", new(description, company, product, original))
            .File("Game.ini").File("Data.wolf").File("data.dts").File("system.dat");
        var result = Assert.Single(EngineDetectorSet.Default.DetectAll(snapshot).Results, result => result.Confidence == DetectionConfidence.High);
        Assert.Equal(expected, result.Engine);
        Assert.Equal("Example.exe", Assert.Single(result.EntryCandidates).RelativePath);
        var misleading = new InMemorySnapshot().File(expected + ".exe").File("Game.ini");
        Assert.DoesNotContain(EngineDetectorSet.Default.DetectAll(misleading).Results, result => result.Confidence == DetectionConfidence.High);
    }

    [Fact]
    public void InsufficientAndConflictingEvidence_IsNotPromotedToAUniqueEngine()
    {
        var incomplete = new InMemorySnapshot().Identity("Game.exe", new("SRPG Studio Game Engine", "SapphireSoft", null, null));
        Assert.DoesNotContain(EngineDetectorSet.Default.DetectAll(incomplete).Results, result => result.Engine == EngineId.SrpgStudio);
        var conflict = incomplete.Directory("Game_Data").File("UnityPlayer.dll").File("Game_Data/globalgamemanagers")
            .File("data.dts").File("system.dat");
        Assert.Equal(2, EngineDetectorSet.Default.DetectAll(conflict).Conflict!.ConflictingResults.Count);
    }

    [Fact]
    public void Unity_MultipleDataDirectories_UsesThePairedGameInsteadOfTheFirstDirectory()
    {
        var snapshot = new InMemorySnapshot().Directory("A_Data").Directory("Game_Data")
            .File("Game.exe").File("Game_Data/globalgamemanagers");
        var result = Assert.Single(EngineDetectorSet.Default.DetectAll(snapshot).Results, result => result.Engine == EngineId.Unity);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
        Assert.True(Assert.Single(result.EntryCandidates).Score >= EntryCandidate.PrescoreThreshold);
        Assert.Contains(result.Evidence, evidence => evidence.RelativePath == "Game_Data/globalgamemanagers" && evidence.Observation == EvidenceObservation.Present);
    }

    [Theory]
    [InlineData("TVP(KIRIKIRI) 2 core / Scripting Platform for Win32")]
    [InlineData("TVP(KIRIKIRI) Z core / Scripting Platform for Win32")]
    public void Kirikiri_EmbeddedGame_UsesItsActualVersionIdentity(string description)
    {
        var snapshot = new InMemorySnapshot().Identity("Example.exe", new(description, null, null, "tvpwin32.exe"));
        var result = Assert.Single(EngineDetectorSet.Default.DetectAll(snapshot).Results);
        Assert.Equal(EngineId.Kirikiri, result.Engine);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
    }

    [Fact]
    public void EmptyUnrealFoldersAndTruncatedGodotHeaders_AreNotGameEvidence()
    {
        var empty = new InMemorySnapshot().File("Game.exe").Directory("Engine").Directory("Example")
            .Directory("Example/Content/Paks").Directory("Example/Binaries/Win64")
            .File("Game.pck", [0x47, 0x44, 0x50, 0x43]);
        Assert.DoesNotContain(EngineDetectorSet.Default.DetectAll(empty).Results, result => result.Confidence == DetectionConfidence.High);
    }

    [Fact]
    public void GodotEmbeddedPack_TrailerAndHeaderEvidence_DetectsEngine()
    {
        var snapshot = new InMemorySnapshot().FileContent("Game.exe", EmbeddedGodotExecutable());
        var result = Assert.Single(EngineDetectorSet.Default.DetectAll(snapshot).Results, result => result.Engine == EngineId.Godot);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
        Assert.Equal("Game.exe", Assert.Single(result.EntryCandidates).RelativePath);
        Assert.Contains(result.Evidence, evidence => evidence.RelativePath == "Game.exe");
        Assert.DoesNotContain(EngineDetectorSet.Default.DetectAll(new InMemorySnapshot().FileContent("Game.exe", EmbeddedGodotExecutable(packFormat: 9))).Results,
            result => result.Engine == EngineId.Godot);
    }

    [Fact]
    public void GodotMagicLookingCodeAndTruncatedTrailers_AreNotEmbeddedPackEvidence()
    {
        var code = new byte[128];
        "GDPC"u8.CopyTo(code.AsSpan(8));
        "GDPC"u8.CopyTo(code.AsSpan(64));
        Assert.DoesNotContain(EngineDetectorSet.Default.DetectAll(new InMemorySnapshot().FileContent("Game.exe", code)).Results,
            result => result.Engine == EngineId.Godot);
        var truncated = new byte[64];
        "GDPC"u8.CopyTo(truncated.AsSpan(60));
        Assert.DoesNotContain(EngineDetectorSet.Default.DetectAll(new InMemorySnapshot().FileContent("Game.exe", truncated)).Results,
            result => result.Engine == EngineId.Godot);
    }

    private static byte[] EmbeddedGodotExecutable(uint packFormat = 3, uint major = 4, uint minor = 6, uint patch = 2)
    {
        var pack = new byte[64];
        "GDPC"u8.CopyTo(pack);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(4), packFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(8), major);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(12), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(16), patch);
        var executable = new byte[16 + pack.Length + 12];
        pack.CopyTo(executable, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(executable.AsSpan(executable.Length - 12), (ulong)pack.Length);
        "GDPC"u8.CopyTo(executable.AsSpan(executable.Length - 4));
        return executable;
    }
}
