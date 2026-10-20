using System.Buffers.Binary;
using System.Text;

namespace GameLibrary.Domain.Detection.Detectors;

/// <summary>配对文件与 PE 版本资源共同判定；不按文件夹名或工具 Hook 名猜测引擎。</summary>
public sealed class AdditionalEngineDetector(EngineId engine) : IEngineDetector
{
    public EngineId Engine => engine;
    public int DetectorVersion => 1;

    public DetectionResult? Detect(IDirectorySnapshot snapshot)
    {
        var files = snapshot.ListFiles("");
        if (files.Count > 256) return null;
        var entries = files.Where(LaunchSuggestionDetector.IsGameEntryName).ToArray();
        if (entries.Length == 0) return null;
        var evidence = new List<DetectionEvidence>();
        void Found(string rule, string path) => evidence.Add(new(rule, path, EvidenceObservation.Present, EvidencePolarity.Positive));
        bool Has(string path) => snapshot.FileExists(path);

        if (engine is EngineId.RpgMakerXp or EngineId.RpgMakerVx or EngineId.RpgMakerVxAce)
        {
            var probe = snapshot.TryReadFirstBytes("Game.ini", 8192);
            if (probe.Kind != FileProbeKind.Ok) return null;
            var ini = Encoding.UTF8.GetString(probe.Bytes!);
            var library = ini.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Library=", StringComparison.OrdinalIgnoreCase))?[8..].Trim();
            var prefix = engine switch { EngineId.RpgMakerXp => "RGSS1", EngineId.RpgMakerVx => "RGSS2", _ => "RGSS3" };
            var archive = engine switch { EngineId.RpgMakerXp => ".rgssad", EngineId.RpgMakerVx => ".rgss2a", _ => ".rgss3a" };
            var scripts = engine switch { EngineId.RpgMakerXp => "Data/Scripts.rxdata", EngineId.RpgMakerVx => "Data/Scripts.rvdata", _ => "Data/Scripts.rvdata2" };
            if (library is null || !Path.GetFileName(library.Replace('\\', '/')).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || !Has(scripts) && !files.Any(file => file.EndsWith(archive, StringComparison.OrdinalIgnoreCase))) return null;
            Found("rgss.library", "Game.ini");
            Found("rgss.scripts", Has(scripts) ? scripts : files.First(file => file.EndsWith(archive, StringComparison.OrdinalIgnoreCase)));
        }
        else if (engine == EngineId.Godot)
        {
            var pack = files.FirstOrDefault(file => file.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)
                && snapshot.TryReadFirstBytes(file, 20).Bytes is { Length: 20 } bytes && IsGodotPackHeader(bytes));
            var identified = entries.Where(entry => snapshot.ReadExecutableIdentity(entry)?.Product?.Contains("Godot Engine", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            var embedded = entries.Where(entry => IsGodotEmbeddedPack(snapshot, entry)).ToArray();
            if (pack is null && identified.Length == 0 && embedded.Length == 0) return null;
            if (pack is not null) Found("godot.pack-header", pack);
            if (identified.Length > 0) { entries = identified; Found("godot.pe-product", identified[0]); }
            else if (embedded.Length > 0) { entries = embedded; Found("godot.embedded-pack", embedded[0]); }
        }
        else if (engine == EngineId.Unreal)
        {
            if (!snapshot.DirectoryExists("Engine")) return null;
            var projects = snapshot.ListDirectories("").Take(32).Where(dir =>
                snapshot.ListFiles(dir + "/Content/Paks").Any(file => file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) && Has(dir + "/Content/Paks/" + Path.ChangeExtension(file, ".ucas")))
                && new[] { "Win64", "Win32" }.Any(platform => snapshot.ListFiles(dir + "/Binaries/" + platform).Any(LaunchSuggestionDetector.IsGameEntryName))).ToArray();
            if (projects.Length != 1) return null;
            Found("unreal.packaged-project", projects[0] + "/Content/Paks");
            Found("unreal.engine-runtime", "Engine");
        }
        else if (engine == EngineId.Tyrano)
        {
            var prefix = new[] { "", "www/" }.FirstOrDefault(path => Has(path + "tyrano/tyrano.js") && Has(path + "data/system/Config.tjs") && Has(path + "index.html"));
            if (prefix is null) return null;
            Found("tyrano.runtime", prefix + "tyrano/tyrano.js");
            Found("tyrano.config", prefix + "data/system/Config.tjs");
        }
        else
        {
            entries = entries.Where(entry => MatchesIdentity(snapshot.ReadExecutableIdentity(entry), snapshot)).ToArray();
            if (entries.Length == 0) return null;
            foreach (var entry in entries) Found("engine.pe-identity", entry);
        }
        return new DetectionResult
        {
            Engine = engine,
            DetectorVersion = DetectorVersion,
            Confidence = DetectionConfidence.High,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = entries.Select(entry => new EntryCandidate(entry,
                EntryScoring.EngineStructureBonus + EntryScoring.GameExeNameBonusFor(entry), ["engine-file-evidence"]))
                .OrderByDescending(entry => entry.Score).ThenBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray(),
        };
    }

    private bool MatchesIdentity(ExecutableIdentity? identity, IDirectorySnapshot snapshot) => identity is not null && engine switch
    {
        EngineId.SrpgStudio => identity.Description == "SRPG Studio Game Engine" && identity.Company == "SapphireSoft"
            && snapshot.FileExists("data.dts") && snapshot.FileExists("system.dat"),
        EngineId.PixelGameMakerMv => identity.Description == "Pixel Game Maker MV player" && identity.Company == "KADOKAWA CORPORATION",
        EngineId.RpgDeveloperBakin => identity.Product == "RPG Developer Bakin" && identity.OriginalFileName == "bakinplayer_launcher.exe",
        EngineId.WolfRpg => (identity.Product?.Contains("WOLF RPG", StringComparison.OrdinalIgnoreCase) == true
            || identity.Description?.Contains("WOLF RPG", StringComparison.OrdinalIgnoreCase) == true)
            && snapshot.FileExists("Game.ini") && (snapshot.FileExists("Data.wolf") || snapshot.DirectoryExists("Data/BasicData")),
        _ => false,
    };

    private static bool IsGodotPackHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("GDPC"u8)
        && BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) is >= 1 and <= 4
        && BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]) is >= 2 and <= 4;

    /// <summary>
    /// Godot 4 内嵌 PCK：独立导出包用 .pck 文件；启用内嵌时文件尾为 [uint64 包大小][GDPC]，
    /// 包头在"包大小 + 12 字节尾部"之前。只做两次定长读取，不扫描整个可执行文件。
    /// </summary>
    private static bool IsGodotEmbeddedPack(IDirectorySnapshot snapshot, string relativePath)
    {
        if (snapshot.FileSize(relativePath) is not { } size || size < 40) return false;
        var tail = snapshot.TryReadBytesAt(relativePath, size - 12, 12);
        if (tail.Bytes is not { Length: 12 } trailer || !trailer.AsSpan(8, 4).SequenceEqual("GDPC"u8)) return false;
        var packSize = BinaryPrimitives.ReadUInt64LittleEndian(trailer.AsSpan(0, 8));
        if (packSize is < 20 || packSize > (ulong)(size - 12)) return false;
        foreach (var offset in new[] { size - 12 - (long)packSize, size - (long)packSize })
        {
            var header = snapshot.TryReadBytesAt(relativePath, offset, 20);
            if (header.Bytes is { Length: 20 } bytes && IsGodotPackHeader(bytes)) return true;
        }

        return false;
    }
}
