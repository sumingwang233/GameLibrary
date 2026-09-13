using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLibrary.Contracts;

/// <summary>三入口共用的 JSON 序列化选项；属性 camelCase、枚举固定 camelCase 字符串。</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
