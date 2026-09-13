namespace GameLibrary.Host.Observability;

/// <summary>
/// 日志脱敏（补充规格 4.3）：按已知敏感位置替换占位符，不做"搜 token 字样"式过滤。
/// 完整 argv/cwd 不进入审计；message/detail 字段经 Sanitize 后写入。
/// </summary>
public static class LogSanitizer
{
    /// <summary>替换已知敏感前缀：数据目录 → {dataDir}、用户主目录 → {userProfile}；全部出现都替换。</summary>
    public static string Sanitize(string? message, string? dataDirectory = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? "";
        }

        var result = message;
        if (!string.IsNullOrEmpty(dataDirectory))
        {
            result = ReplacePrefix(result, dataDirectory, "{dataDir}");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            result = ReplacePrefix(result, userProfile, "{userProfile}");
        }

        return result;
    }

    private static string ReplacePrefix(string text, string prefix, string placeholder)
    {
        var normalized = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return text
            .Replace(normalized + Path.DirectorySeparatorChar, placeholder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace(normalized + Path.AltDirectorySeparatorChar, placeholder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace(normalized, placeholder, StringComparison.OrdinalIgnoreCase);
    }
}
