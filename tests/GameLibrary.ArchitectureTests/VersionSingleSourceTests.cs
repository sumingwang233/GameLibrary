using System.Text.RegularExpressions;
using Xunit;

namespace GameLibrary.ArchitectureTests;

/// <summary>
/// 版本号单一真源门禁（R38）：Directory.Build.props 的 &lt;Version&gt; 是 .NET 侧唯一声明；
/// Cargo.toml 与 package.json 因生态各自必需，但必须与真源一致；
/// tauri.conf.json 不声明版本（继承 Cargo.toml）。
/// </summary>
public sealed partial class VersionSingleSourceTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.False(directory is null, "未找到仓库根（向上遍历未命中 Directory.Build.props）");
        return directory!.FullName;
    }

    [GeneratedRegex(@"<Version>\s*([^<]+?)\s*</Version>")]
    private static partial Regex PropsVersionRegex();

    private static string SingleSourceVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var match = PropsVersionRegex().Match(props);
        Assert.True(match.Success, "Directory.Build.props 缺少 <Version> 声明（单一真源）");
        return match.Groups[1].Value;
    }

    [Fact]
    public void DotNetProjects_InheritVersionFromDirectoryBuildProps()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(project).Contains("<Version>", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(root, project));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"以下 csproj 不得自行声明 <Version>（真源在 Directory.Build.props）：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void TauriDeclarations_AgreeWithSingleSource()
    {
        var root = RepoRoot();
        var version = SingleSourceVersion();
        var tauri = Path.Combine(root, "src", "GameLibrary.Tauri");

        var cargo = File.ReadAllText(Path.Combine(tauri, "src-tauri", "Cargo.toml"));
        var cargoMatch = Regex.Match(cargo, @"^version\s*=\s*""([^""]+)""", RegexOptions.Multiline);
        Assert.True(cargoMatch.Success, "Cargo.toml 缺少 [package] version 声明");
        Assert.True(
            cargoMatch.Groups[1].Value == version,
            $"Cargo.toml 版本 {cargoMatch.Groups[1].Value} 与单一真源 {version} 不一致");

        var packageJson = File.ReadAllText(Path.Combine(tauri, "package.json"));
        var packageMatch = Regex.Match(packageJson, @"""version""\s*:\s*""([^""]+)""");
        Assert.True(packageMatch.Success, "package.json 缺少 version 声明");
        Assert.True(
            packageMatch.Groups[1].Value == version,
            $"package.json 版本 {packageMatch.Groups[1].Value} 与单一真源 {version} 不一致");

        var tauriConf = File.ReadAllText(Path.Combine(tauri, "src-tauri", "tauri.conf.json"));
        Assert.False(
            tauriConf.Contains("\"version\"", StringComparison.Ordinal),
            "tauri.conf.json 不应声明 version（继承 Cargo.toml；真源在 Directory.Build.props）");
    }
}
