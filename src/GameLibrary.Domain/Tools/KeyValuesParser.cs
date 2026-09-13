using System.Text;

namespace GameLibrary.Domain.Tools;

/// <summary>
/// Valve KeyValues 最小解析器（策划案 11）：支持嵌套 { }、带引号与裸键值、
/// \" 转义、// 与 /* 注释；输入限 8 MiB。不做正则整体匹配。
/// </summary>
public static class KeyValuesParser
{
    public const int MaxInputChars = 8 * 1024 * 1024;

    public sealed class KvObject
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetString(string key) => Values.TryGetValue(key, out var v) && v is string s ? s : null;

        public KvObject? GetObject(string key) => Values.TryGetValue(key, out var v) && v is KvObject o ? o : null;
    }

    public static KvObject Parse(string text)
    {
        if (text.Length > MaxInputChars)
        {
            throw new ArgumentException($"输入超过 {MaxInputChars} 字符上限");
        }

        var index = 0;
        return ParseObject(text, ref index, topLevel: true);
    }

    private static KvObject ParseObject(string text, ref int index, bool topLevel)
    {
        var result = new KvObject();
        while (index < text.Length)
        {
            SkipWhitespaceAndComments(text, ref index);
            if (index >= text.Length)
            {
                break;
            }

            if (text[index] == '}')
            {
                if (topLevel)
                {
                    throw new FormatException("多余的右花括号");
                }

                index++;
                return result;
            }

            var key = ReadToken(text, ref index);
            SkipWhitespaceAndComments(text, ref index);
            if (index < text.Length && text[index] == '{')
            {
                index++;
                result.Values[key] = ParseObject(text, ref index, topLevel: false);
                continue;
            }

            result.Values[key] = ReadToken(text, ref index);
        }

        if (!topLevel)
        {
            throw new FormatException("嵌套对象未闭合");
        }

        return result;
    }

    private static void SkipWhitespaceAndComments(string text, ref int index)
    {
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            if (c == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    while (index < text.Length && text[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (text[index + 1] == '*')
                {
                    var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? text.Length : end + 2;
                    continue;
                }
            }

            return;
        }
    }

    private static string ReadToken(string text, ref int index)
    {
        SkipWhitespaceAndComments(text, ref index);
        if (index >= text.Length)
        {
            throw new FormatException("期待键或值但输入已结束");
        }

        if (text[index] == '"')
        {
            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index];
                if (c == '\\' && index + 1 < text.Length)
                {
                    builder.Append(text[index + 1] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        var other => other,
                    });
                    index += 2;
                    continue;
                }

                if (c == '"')
                {
                    index++;
                    return builder.ToString();
                }

                builder.Append(c);
                index++;
            }

            throw new FormatException("引号未闭合");
        }

        // 裸 token：读到空白、{、}。
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '{' && text[index] != '}')
        {
            index++;
        }

        if (index == start)
        {
            throw new FormatException($"无法解析的字符：{text[index]}");
        }

        return text[start..index];
    }
}

/// <summary>Steam 入口规则（策划案 11）：appid 十进制正整数；steam:// 协议仅交 Shell 且只允许验证后数字。</summary>
public static class SteamRules
{
    public const string ToolId = "steam";

    public static bool IsValidAppId(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.All(char.IsDigit)
        && value[0] != '0'
        && value.Length <= 10
        && ulong.Parse(value) > 0;

    public static IReadOnlyDictionary<string, string> SteamCapability() => new Dictionary<string, string>
    {
        ["canLaunch"] = nameof(ToolCapabilityState.Supported),
        ["canRequestInjection"] = nameof(ToolCapabilityState.Unsupported),
        ["canDeploy"] = nameof(ToolCapabilityState.Unsupported),
        ["canRollback"] = nameof(ToolCapabilityState.Unsupported),
        ["mayUseNetwork"] = nameof(ToolCapabilityState.Supported),
    };
}

/// <summary>外部播放器能力声明（策划案 11）：启动本地文件为公开行为；参数差异须逐播放器验证。</summary>
public static class PlayerCapability
{
    public const string ToolId = "player";

    public static IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["canLaunch"] = nameof(ToolCapabilityState.Supported),
        ["canRequestInjection"] = nameof(ToolCapabilityState.Unsupported),
        ["canDeploy"] = nameof(ToolCapabilityState.Unsupported),
        ["canRollback"] = nameof(ToolCapabilityState.Unsupported),
        ["mayUseNetwork"] = nameof(ToolCapabilityState.Unknown),
    };
}
