using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GameLibrary.Desktop;

internal sealed record GitHubReleaseInfo(Version Version, string TagName, string Url);

/// <summary>从公开 GitHub Releases 查询最新稳定版；失败时由调用方静默降级。</summary>
internal static class GitHubReleaseChecker
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/sumingwang233/GameLibrary/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(GitHubReleaseChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return version.ToString(3);
        }
    }

    public static async Task<GitHubReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(LatestReleaseUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.GetProperty("html_url").GetString() ?? "";
        var version = ParseVersionTag(tagName);
        return version is null || url.Length == 0
            ? null
            : new GitHubReleaseInfo(version, tagName, url);
    }

    internal static Version? ParseVersionTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        var value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            value = value[..suffix];
        }

        return Version.TryParse(value, out var version) ? version : null;
    }

    internal static bool IsNewer(Version latest, string currentVersion) =>
        Version.TryParse(currentVersion, out var current) && latest > current;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameLibrary", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
