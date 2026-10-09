using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLibrary.Host.Hosting;

/// <summary>Use XUnity's existing font settings; never install fonts into Windows or replace game assets.</summary>
internal static partial class UnityTranslationFonts
{
    internal const string FontDirectory = "GameLibraryFonts";
    internal static string? SystemFont => new[]
    {
        ("msyh.ttc", "Microsoft YaHei"), ("simhei.ttf", "SimHei"),
        ("simsun.ttc", "SimSun"), ("arialuni.ttf", "Arial Unicode MS")
    }.FirstOrDefault(item => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), item.Item1))).Item2;

    internal static string? Generation(UnityTranslationLayout layout)
    {
        foreach (var file in new[] { Path.Combine(layout.Data, "globalgamemanagers"), Path.Combine(layout.Data, "data.unity3d") })
        {
            UnityTranslationInspection.RejectReparse(layout.Root, file);
            if (!File.Exists(file)) continue;
            using var stream = File.OpenRead(file);
            var bytes = new byte[256];
            var count = stream.Read(bytes);
            var match = VersionPattern().Match(Encoding.ASCII.GetString(bytes, 0, count));
            if (match.Success) return Supported(match.Groups[1].Value);
        }
        var player = Path.Combine(Path.GetDirectoryName(layout.ExecutablePath)!, "UnityPlayer.dll");
        UnityTranslationInspection.RejectReparse(layout.Root, player);
        if (!File.Exists(player)) return null;
        var version = FileVersionInfo.GetVersionInfo(player).FileVersion;
        return version is null ? null : Supported(version.Split('.')[0]);
    }

    private static string? Supported(string value) => value is "2018" or "2019" or "2020" or "2021" or "2022" or "2023" or "6000" ? value : null;
    internal static string BundlePath(string generation) => FontDirectory + "/xiaolai-" + generation + ".bundle";

    internal static bool NeedsRepair(UnityTranslationLayout layout, UnityTranslationIni ini)
    {
        if (SystemFont is not null && string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFont"))) return true;
        if (!string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFontTextMeshPro"))) return false;
        var fallback = ini.Get("Behaviour", "FallbackFontTextMeshPro");
        var generation = Generation(layout);
        return generation is not null && (string.IsNullOrWhiteSpace(fallback)
            || fallback == BundlePath(generation) && !File.Exists(Path.Combine(layout.Root, fallback)));
    }

    internal static bool Configure(UnityTranslationLayout layout, UnityTranslationIni ini, out string? generation)
    {
        if (string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFont")) && SystemFont is { } font)
            ini.Set("Behaviour", "OverrideFont", font);
        generation = Generation(layout);
        if (generation is null || !string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFontTextMeshPro"))) return false;
        var fallback = ini.Get("Behaviour", "FallbackFontTextMeshPro");
        if (!string.IsNullOrWhiteSpace(fallback) && fallback != BundlePath(generation)) return false;
        ini.Set("Behaviour", "FallbackFontTextMeshPro", BundlePath(generation));
        return true;
    }

    [GeneratedRegex(@"(?<![0-9])((?:20[0-9]{2})|6000)\.[0-9]+\.[0-9]+[abfp][0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
