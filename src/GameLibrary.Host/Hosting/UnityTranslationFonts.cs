using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
        if (WantsSystemTmp(layout, ini) && SupportsSystemTmpPlugin(layout.Core))
            return ini.Get("Behaviour", "OverrideFontTextMeshPro") != SystemFont
                || !string.IsNullOrWhiteSpace(ini.Get("Behaviour", "FallbackFontTextMeshPro"))
                || string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFont"))
                || BundleGeneration(layout, ini.Get("Behaviour", "OverrideFont")) is not null;
        if (BundleGeneration(layout, ini.Get("Behaviour", "OverrideFont")) is not null) return true;
        if (SystemFont is not null && string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFont"))) return true;
        var generation = Generation(layout);
        if (generation is null) return false;
        if (IncompatibleBundle(layout, ini.Get("Behaviour", "OverrideFontTextMeshPro"), generation)
            || IncompatibleBundle(layout, ini.Get("Behaviour", "FallbackFontTextMeshPro"), generation)) return true;
        var bundle = BundlePath(generation);
        var font = ini.Get("Behaviour", "OverrideFontTextMeshPro");
        var fallback = ini.Get("Behaviour", "FallbackFontTextMeshPro");
        if (!string.IsNullOrWhiteSpace(font))
            return font == bundle && !File.Exists(Path.Combine(layout.Root, bundle));
        if (!string.IsNullOrWhiteSpace(fallback) && fallback != bundle) return false;
        // Mono TMP versions can lack the global fallback API; use the supported font override.
        return layout.Runtime == "mono" || string.IsNullOrWhiteSpace(fallback)
            || !File.Exists(Path.Combine(layout.Root, bundle));
    }

    internal static bool Configure(UnityTranslationLayout layout, UnityTranslationIni ini, out string? generation, bool upgradedPlugin = false)
    {
        if (WantsSystemTmp(layout, ini) && (upgradedPlugin || SupportsSystemTmpPlugin(layout.Core)))
        {
            if (string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFont"))
                || BundleGeneration(layout, ini.Get("Behaviour", "OverrideFont")) is not null)
                ini.Set("Behaviour", "OverrideFont", SystemFont!);
            ini.Set("Behaviour", "OverrideFontTextMeshPro", SystemFont!);
            ini.Set("Behaviour", "FallbackFontTextMeshPro", "");
            generation = null;
            return false; // Native TMP creates the font/material/atlas for this game version, no foreign AssetBundle.
        }
        // UGUI expects an OS font family, not a TMP AssetBundle filename.
        if (BundleGeneration(layout, ini.Get("Behaviour", "OverrideFont")) is not null)
            ini.Set("Behaviour", "OverrideFont", SystemFont ?? "");
        if (string.IsNullOrWhiteSpace(ini.Get("Behaviour", "OverrideFont")) && SystemFont is { } font)
            ini.Set("Behaviour", "OverrideFont", font);
        generation = Generation(layout);
        if (generation is null) return false;
        foreach (var setting in new[] { "OverrideFontTextMeshPro", "FallbackFontTextMeshPro" })
            if (IncompatibleBundle(layout, ini.Get("Behaviour", setting), generation)) ini.Set("Behaviour", setting, "");
        var bundle = BundlePath(generation);
        var primary = ini.Get("Behaviour", "OverrideFontTextMeshPro");
        if (!string.IsNullOrWhiteSpace(primary)) return primary == bundle;
        var fallback = ini.Get("Behaviour", "FallbackFontTextMeshPro");
        if (!string.IsNullOrWhiteSpace(fallback) && fallback != bundle) return false;
        if (layout.Runtime == "mono")
        {
            ini.Set("Behaviour", "OverrideFontTextMeshPro", bundle);
            // Do not load the same large bundle twice through XUnity's separate font caches.
            ini.Set("Behaviour", "FallbackFontTextMeshPro", "");
        }
        else ini.Set("Behaviour", "FallbackFontTextMeshPro", bundle);
        return true;
    }

    private static bool IncompatibleBundle(UnityTranslationLayout layout, string? value, string generation) =>
        SupportsSystemTmp(layout) && BundleGeneration(layout, value) is { } actual && actual != generation;

    private static string? BundleGeneration(UnityTranslationLayout layout, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value)) return null;
        var path = Path.GetFullPath(Path.Combine(layout.Root, value));
        if (!path.StartsWith(layout.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        UnityTranslationInspection.RejectReparse(layout.Root, path);
        if (!File.Exists(path)) return null; // Unresolved custom resource names are preserved.
        using var stream = File.OpenRead(path);
        var bytes = new byte[128];
        var count = stream.Read(bytes);
        var header = Encoding.ASCII.GetString(bytes, 0, count);
        if (!header.StartsWith("UnityFS\0", StringComparison.Ordinal)) return null;
        var match = VersionPattern().Match(header);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static bool NeedsPluginRepair(UnityTranslationLayout layout) => layout.Loader == "rei"
        && WantsSystemTmp(layout, UnityTranslationIni.Read(layout.Config)) && !SupportsSystemTmpPlugin(layout.Core);

    private static bool WantsSystemTmp(UnityTranslationLayout layout, UnityTranslationIni ini)
    {
        if (!SupportsSystemTmp(layout)) return false;
        var generation = Generation(layout);
        bool ManagedOrInvalid(string? value) => string.IsNullOrWhiteSpace(value) || value == SystemFont
            || value.StartsWith(FontDirectory + "/xiaolai-", StringComparison.Ordinal)
            || generation is not null && IncompatibleBundle(layout, value, generation);
        return ManagedOrInvalid(ini.Get("Behaviour", "OverrideFontTextMeshPro"))
            && ManagedOrInvalid(ini.Get("Behaviour", "FallbackFontTextMeshPro"));
    }

    internal static Version? PluginVersion(string core)
    {
        using var stream = File.OpenRead(core);
        using var pe = new PEReader(stream);
        return pe.HasMetadata ? pe.GetMetadataReader().GetAssemblyDefinition().Version : null;
    }

    private static bool SupportsSystemTmpPlugin(string core) => File.Exists(core) && PluginVersion(core) >= new Version(5, 6, 2, 0);

    internal static bool SupportsSystemTmp(UnityTranslationLayout layout)
    {
        if (layout.Runtime != "mono" || SystemFont is null) return false;
        foreach (var name in new[] { "Unity.TextMeshPro.dll", "TMPro.dll" })
        {
            var path = Path.Combine(layout.Managed, name);
            UnityTranslationInspection.RejectReparse(layout.Root, path);
            if (!File.Exists(path)) continue;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var metadata = pe.GetMetadataReader();
            foreach (var type in metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
                .Where(t => metadata.GetString(t.Namespace) == "TMPro" && metadata.GetString(t.Name) == "TMP_FontAsset"))
                foreach (var method in type.GetMethods().Select(metadata.GetMethodDefinition))
                {
                    if (metadata.GetString(method.Name) != "CreateFontAsset") continue;
                    if ((method.Attributes & (System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static))
                        != (System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static)) continue;
                    var signature = metadata.GetBlobBytes(method.Signature);
                    // Public static CreateFontAsset(string family, string style, int pointSize), TMP 3.2+.
                    if (signature.Length >= 6 && signature[0] == 0 && signature[1] == 3
                        && signature.AsSpan().EndsWith(new byte[] { 0x0e, 0x0e, 0x08 })) return true;
                }
        }
        return false;
    }

    [GeneratedRegex(@"(?<![0-9])((?:20[0-9]{2})|6000)\.[0-9]+\.[0-9]+[abfp][0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
