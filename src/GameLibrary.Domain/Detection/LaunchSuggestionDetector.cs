using System.Text.RegularExpressions;
using GameLibrary.Domain.Detection.Detectors;

namespace GameLibrary.Domain.Detection;

/// <summary>只使用目录证据推荐入口，不执行程序，也不把文件名当作引擎类型。</summary>
public static partial class LaunchSuggestionDetector
{
    private static readonly EngineDetectorSet Detectors = new(
        [new UnityDetector(), new RpgMakerMvMzDetector(), new RenpyDetector(), new KirikiriDetector()]);

    private static readonly string[] Helpers =
        ["inst", "uninst", "settings", "config", "エンジン設定", "セーブファイル設定", "アンインストール"];

    [GeneratedRegex(@"(?i)(?:[_\- .](?:chs|cht|cn|zh|chinese)(?:[_\- .]|$)|汉化|漢化|中文|简体|簡體|繁体|繁體)")]
    private static partial Regex ChineseMarker();

    public static bool IsGameEntryName(string name) => !name.Contains('/') && !name.Contains('\\')
        && Path.GetExtension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        && !EntryScoring.IsExcludedEntryName(name)
        && !Helpers.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase)
        && !new[] { "crashpad", "crashreport", "dxsetup", "notification_helper", "unitybugreporter", "vcredist" }
            .Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<EntryCandidate> Detect(IDirectorySnapshot snapshot)
    {
        var files = snapshot.ListFiles("");
        if (files.Count > 256) return [];
        var names = files.Where(IsGameEntryName).ToArray();
        var engineEntries = Detectors.DetectAll(snapshot).Results.SelectMany(result => result.EntryCandidates)
            .GroupBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.MaxBy(entry => entry.Score)!, StringComparer.OrdinalIgnoreCase);
        var result = new List<EntryCandidate>();
        foreach (var name in names)
        {
            engineEntries.TryGetValue(name, out var engineEntry);
            var score = engineEntry?.Score ?? 20;
            var reasons = new List<string>(engineEntry?.Reasons ?? []);
            var stem = Path.GetFileNameWithoutExtension(name);
            var marker = ChineseMarker().Match(stem);
            if (marker.Success)
            {
                score += 40;
                reasons.Add("chinese-name");
                var original = stem[..marker.Index].TrimEnd('_', '-', ' ', '.') + ".exe";
                if (names.Contains(original, StringComparer.OrdinalIgnoreCase))
                {
                    score = engineEntries.TryGetValue(original, out var paired) ? Math.Max(score, Math.Max(40, paired.Score) + 40) : score + 40;
                    reasons.Add("chinese-original-pair");
                }
            }
            if (names.Length == 1)
            {
                score = Math.Max(80, score);
                reasons.Add("single-entry");
            }
            else if (!marker.Success && (stem.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("app", StringComparison.OrdinalIgnoreCase)))
            {
                // Generic inner binaries can coexist with a required game-specific launcher.
                score = Math.Min(score, 55);
                reasons.Add("multiple-launch-stages");
            }
            result.Add(new EntryCandidate(name, score, reasons));
        }
        return result.OrderByDescending(entry => entry.Score).ThenBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }
}
