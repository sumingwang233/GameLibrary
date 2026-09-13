using GameLibrary.Domain.Tools;

namespace GameLibrary.Infrastructure.Tools;

/// <summary>
/// Steam 适配（任务书 T09，策划案 11）：只读发现 Steam 安装（注册表只读 SteamPath，
/// 回退常见目录）、解析 steamapps/appmanifest_*.acf（KeyValues）、生成
/// `steam.exe -applaunch <appid>` 启动模板（Valve 公开稳定参数）。
/// appid 仅取自本地清单并校验十进制正整数；无清单 → SteamManifestMissing，不凭目录名硬填。
/// steam://rungameid 网页协议不在此生成（须 Windows Shell 集成验收）。
/// </summary>
public sealed class SteamAdapter
{
    private static readonly string[] FallbackRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
    ];

    public const string ManifestMissingNote = "SteamManifestMissing：steamapps 下无 appmanifest 清单；不凭目录名硬填 appid";

    /// <summary>发现 Steam 安装与已装应用清单；清单缺失如实标注。</summary>
    public SteamDiscovery Discover()
    {
        var steamRoot = LocateSteamRoot();
        if (steamRoot is null || !File.Exists(Path.Combine(steamRoot, "steam.exe")))
        {
            return new SteamDiscovery
            {
                Found = false,
                Capability = SteamRules.SteamCapability(),
                Notice = "未发现 Steam 安装（注册表与常见路径均无 steam.exe）",
            };
        }

        var steamApps = Path.Combine(steamRoot, "steamapps");
        var manifests = new List<SteamAppManifest>();
        if (Directory.Exists(steamApps))
        {
            foreach (var file in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    // 单清单限 8 MiB（KeyValuesParser 内部同样限制）。
                    if (new FileInfo(file).Length > KeyValuesParser.MaxInputChars)
                    {
                        continue;
                    }

                    var parsedRoot = KeyValuesParser.Parse(File.ReadAllText(file));
                    // ACF 顶层键为 "AppState"（也有无包裹变体）——两形态都取。
                    var parsed = parsedRoot.GetObject("AppState") ?? parsedRoot;
                    var appId = parsed.GetString("appid");
                    if (!SteamRules.IsValidAppId(appId))
                    {
                        continue;
                    }

                    manifests.Add(new SteamAppManifest(
                        appId!,
                        parsed.GetString("name") ?? "(未命名)",
                        parsed.GetString("installdir") ?? "",
                        file));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
                {
                    // 单个清单损坏不影响其余。
                }
            }
        }

        return new SteamDiscovery
        {
            Found = true,
            SteamRoot = steamRoot,
            SteamExecutablePath = Path.Combine(steamRoot, "steam.exe"),
            Manifests = manifests,
            ManifestMissing = manifests.Count == 0,
            Capability = SteamRules.SteamCapability(),
            Notice = manifests.Count == 0
                ? ManifestMissingNote
                : "appid 全部取自本地清单；-applaunch 为 Valve 公开稳定参数，steam:// 协议未经本机验收不生成",
        };
    }

    /// <summary>启动模板（公开稳定参数）：steam.exe -applaunch &lt;appid&gt;，工作目录=Steam 根。</summary>
    public RecipeProcessStep BuildLaunchTemplate(SteamDiscovery discovery, string appId)
    {
        if (!SteamRules.IsValidAppId(appId))
        {
            throw new ArgumentException($"appid 必须是十进制正整数：{appId}");
        }

        if (discovery.Manifests.All(m => m.AppId != appId))
        {
            throw new ArgumentException($"appid 不在本地清单中（SteamManifestMissing 或未安装）：{appId}");
        }

        return new RecipeProcessStep
        {
            Sequence = 0,
            ExecutablePath = discovery.SteamExecutablePath!,
            Arguments = ["-applaunch", appId],
            WorkingDirectory = discovery.SteamRoot!,
            WaitForExit = false,
        };
    }

    /// <summary>只读定位 Steam 根：注册表 HKCU\Software\Valve\Steam\SteamPath，回退常见目录。</summary>
    private static string? LocateSteamRoot()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
            {
                return Path.GetFullPath(steamPath);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
        {
            // 注册表不可读则走常见目录回退。
        }

        return FallbackRoots.FirstOrDefault(Directory.Exists);
    }
}

/// <summary>本地 appmanifest 解析结果。</summary>
public sealed record SteamAppManifest(string AppId, string Name, string InstallDir, string ManifestPath);

/// <summary>tools.discover 的 Steam 结果。</summary>
public sealed record SteamDiscovery
{
    public bool Found { get; init; }

    public string? SteamRoot { get; init; }

    public string? SteamExecutablePath { get; init; }

    public IReadOnlyList<SteamAppManifest> Manifests { get; init; } = [];

    public bool ManifestMissing { get; init; }

    public required IReadOnlyDictionary<string, string> Capability { get; init; }

    public string? Notice { get; init; }
}
