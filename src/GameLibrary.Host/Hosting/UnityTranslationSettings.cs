using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Infrastructure.Backups;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

public sealed record UnityTranslationSettings
{
    public string Provider { get; init; } = "deepseek";
    public string BaseUrl { get; init; } = "https://api.deepseek.com/chat/completions";
    public string Model { get; init; } = "deepseek-flash";
    public string? BoundTagId { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Configured { get; init; }
    public string? CredentialId { get; init; }

    public UnityTranslationSettings Validate()
    {
        if (Provider is not ("deepseek" or "custom")) throw new InvalidDataException("provider 必须为 deepseek 或 custom");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("baseUrl 必须为无凭据的 HTTPS 地址（本机允许 HTTP）");
        if (Provider == "deepseek" && (uri.Host != "api.deepseek.com" || uri.Scheme != "https" || !uri.IsDefaultPort))
            throw new InvalidDataException("DeepSeek provider 仅支持官方 HTTPS 地址");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 128
            || Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
            throw new InvalidDataException("model 必须为 1–128 字符的模型标识符");
        var endpoint = uri.AbsoluteUri.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) endpoint += "/chat/completions";
        return this with { BaseUrl = endpoint };
    }

    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 4096 || key.Any(char.IsControl)
            || key.Trim() != key || key.Contains(' ') || key == "YOUR_API_KEY_HERE")
            throw new InvalidDataException("apiKey 无效；请输入实际密钥，密钥不会返回或写入库");
    }
}

/// <summary>Existing app_settings holds metadata only; provider keys and original game INIs never enter SQLite.</summary>
internal static class UnityTranslationPersistence
{
    internal const string SettingsKey = "unity_translation.settings";
    internal const string StatePrefix = "unity_translation.game.";

    public static T? Read<T>(SqliteLibraryStore store, string key) => store.ReadExclusive((connection, _) =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, ContractJson.Options) : default;
    });

    public static IReadOnlyList<UnityTranslationState> States(SqliteLibraryStore store) => store.ReadExclusive((connection, _) =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key LIKE 'unity_translation.game.%'";
        using var reader = command.ExecuteReader();
        var result = new List<UnityTranslationState>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<UnityTranslationState>(reader.GetString(0), ContractJson.Options) is { } item
                && item.DataEpoch == store.Info.DataEpoch) result.Add(item);
        return result;
    });

    public static void Write<T>(SqliteLibraryStore store, string key, T value) =>
        store.WriteSettingsKeys([(key, JsonSerializer.Serialize(value, ContractJson.Options))], DateTime.UtcNow);
}

/// <summary>CurrentUser DPAPI, UI forbidden; stored in LocalAppData, outside library backups/exports.</summary>
internal sealed class UnityTranslationVault
{
    public string DirectoryPath { get; }

    public UnityTranslationVault(string scope, string? baseDirectory = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        DirectoryPath = Path.Combine(baseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameLibrary", "UnityTranslation", "vault"), hash);
        Directory.CreateDirectory(DirectoryPath);
    }

    public bool HasKey(string? credentialId) => Guid.TryParseExact(credentialId, "N", out _)
        && File.Exists(Path.Combine(DirectoryPath, credentialId + ".bin"));
    public string ReadKey(string? credentialId)
    {
        if (!HasKey(credentialId)) throw new InvalidDataException("当前库引用的本机密钥不存在，请重新导入或配置");
        return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(Path.Combine(DirectoryPath, credentialId + ".bin"))));
    }
    public string WriteKey(string key)
    {
        UnityTranslationSettings.ValidateKey(key);
        var credentialId = Guid.NewGuid().ToString("N");
        AtomicWrite(Path.Combine(DirectoryPath, credentialId + ".bin"), Protect(Encoding.UTF8.GetBytes(key)));
        return credentialId;
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static byte[] Protect(byte[] bytes) => Transform(bytes, protect: true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, protect: false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("当前用户无法读写 Unity 翻译安全存储");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>Only selected keys are updated; unknown mod settings survive. Never serialize an INI into a DTO.</summary>
internal sealed class UnityTranslationIni
{
    private readonly List<string> _lines;
    public UnityTranslationIni(string text) => _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    public static UnityTranslationIni Read(string path)
    {
        if (!File.Exists(path)) return new("");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("翻译配置过大，拒绝读取");
        return new(File.ReadAllText(path));
    }
    public string? Get(string section, string key)
    {
        var current = "";
        string? value = null;
        foreach (var line in _lines)
        {
            var text = line.Trim();
            if (text.StartsWith('[') && text.EndsWith(']')) { current = text[1..^1]; continue; }
            if (text.StartsWith(';') || text.StartsWith('#') || !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var pair = text.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase)) value = pair[1];
        }
        return value;
    }
    public void Set(string section, string key, string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0')) throw new InvalidDataException("配置值包含非法换行");
        var current = "";
        var insertion = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            var text = _lines[i].Trim();
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && insertion < 0) insertion = i;
                current = text[1..^1];
                continue;
            }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var pair = text.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            { _lines.RemoveAt(i--); }
        }
        var header = _lines.FindLastIndex(line => line.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
        if (header < 0) { _lines.Add("[" + section + "]"); _lines.Add(key + "=" + value); }
        else
        {
            var next = _lines.FindIndex(header + 1, line => line.Trim().StartsWith('['));
            _lines.Insert(next < 0 ? _lines.Count : next, key + "=" + value);
        }
    }
    public byte[] Configure(UnityTranslationSettings settings, string key)
    {
        UnityTranslationSettings.ValidateKey(key);
        Set("Service", "Endpoint", "DeepSeekTranslate");
        Set("Service", "FallbackEndpoint", "");
        Set("General", "Language", "zh");
        Set("General", "FromLanguage", "auto");
        Set("DeepSeek", "Endpoint", settings.BaseUrl);
        Set("DeepSeek", "ApiKey", key);
        // Existing models are intentionally preserved unless a different provider is explicitly selected.
        if (settings.Provider == "custom" || string.IsNullOrWhiteSpace(Get("DeepSeek", "Model"))) Set("DeepSeek", "Model", settings.Model);
        Set("DeepSeek", "DisableThinking", settings.Provider == "deepseek" ? "True" : "False");
        Set("DeepSeek", "AddEndingAssistantPrompt", settings.Provider == "deepseek" ? "True" : "False");
        Set("DeepSeek", "Debug", "False");
        Set("DeepSeek", "UseThreadPool", "True");
        return Encoding.UTF8.GetBytes(string.Join("\r\n", _lines));
    }
}
