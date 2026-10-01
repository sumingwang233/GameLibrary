using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace GameLibrary.Desktop;

internal static class L10n
{
    private static readonly Dictionary<string, string[]> Catalog = ReadCatalog();
    public static string Language { get; private set; } = "zh-CN";

    private static Dictionary<string, string[]> ReadCatalog()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("GameLibrary.UiTranslations")
            ?? throw new InvalidDataException("Missing UI translations");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }

    public static string T(string source)
    {
        if (Language == "zh-CN" || !Catalog.TryGetValue(Regex.Replace(source, @"\s+", " ").Trim(), out var entry))
        {
            return source;
        }

        return entry[Language switch { "zh-TW" => 0, "en" => 1, "ja" => 2, _ => 0 }];
    }

    public static string F(FormattableString source) =>
        string.Format(CultureInfo.GetCultureInfo(Language), T(source.Format), source.GetArguments());

    public static void Apply(string? language)
    {
        Language = language is "zh-TW" or "en" or "ja" ? language : "zh-CN";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
        if (Application.Current is null) return;
        foreach (var source in Catalog.Keys)
        {
            Application.Current.Resources["L10n." + Regex.Replace(source, @"\s+", "_")] = T(source);
        }
    }
}
