using GameLibrary.Contracts;
using Xunit;

namespace GameLibrary.ContractTests;

/// <summary>防止 schema.get 的参数名与实际 Host/CLI/MCP 载荷漂移。</summary>
public sealed class OperationSchemaParityTests
{
    [Theory]
    [InlineData("games.create", "sourcePath", "string")]
    [InlineData("games.relink", "newPath", "string")]
    [InlineData("profiles.create", "executablePath", "string")]
    [InlineData("profiles.create", "argv", "array")]
    [InlineData("profiles.update", "executablePath", "string")]
    [InlineData("profiles.update", "argv", "array")]
    public void CoreMutationSchema_UsesActualWireField(string operationId, string field, string type)
    {
        var schema = OperationSchemas.BuildInputSchema(operationId);
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty(field, out var property),
            $"{operationId} schema 缺少实际传输字段 {field}");
        Assert.Equal(type, property.GetProperty("type").GetString());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == field);
    }

    [Fact]
    public void SettingsSchema_AdvertisesDesktopPreferences()
    {
        var properties = OperationSchemas.BuildInputSchema("settings.update")
            .GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("uiFontFamily").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("uiFontScale").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("cacheParentDirectory").GetProperty("type").GetString());
    }

    /// <summary>bug-1：views.create/update 的 sort 描述必须与 games.list 同词表（六值）。</summary>
    [Theory]
    [InlineData("views.create")]
    [InlineData("views.update")]
    public void ViewsSortSchema_AdvertisesGamesListVocabulary(string operationId)
    {
        var description = OperationSchemas.BuildInputSchema(operationId)
            .GetProperty("properties")
            .GetProperty("sort")
            .GetProperty("description")
            .GetString();
        Assert.NotNull(description);
        Assert.Contains("title-asc", description);
        Assert.Contains("title-desc", description);
        Assert.Contains("updated-desc", description);
        Assert.Contains("accepted-desc", description);
    }

    /// <summary>feat-3：tags.create/update 必须声明 category/sortOrder/starred/displayName 四参数。</summary>
    [Theory]
    [InlineData("tags.create")]
    [InlineData("tags.update")]
    public void TagsMutationSchema_AdvertisesClassificationFields(string operationId)
    {
        var properties = OperationSchemas.BuildInputSchema(operationId)
            .GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("category").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("sortOrder").GetProperty("type").GetString());
        // v1.5.2：starred 升级为星级评分 0–5。
        Assert.Equal("integer", properties.GetProperty("starred").GetProperty("type").GetString());
        Assert.Contains("0–5", properties.GetProperty("starred").GetProperty("description").GetString());
        Assert.Equal("string", properties.GetProperty("displayName").GetProperty("type").GetString());
    }

    /// <summary>bug-5：roots.add 必须声明可选 kind（library|manual）；manual 根不参与扫描枚举。</summary>
    [Fact]
    public void RootsAddSchema_AdvertisesManualKind()
    {
        var schema = OperationSchemas.BuildInputSchema("roots.add");
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("kind").GetProperty("type").GetString());
        Assert.Contains("manual", properties.GetProperty("kind").GetProperty("description").GetString());
        // kind 可选：不得进 required（默认 library）。
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "kind");
    }

    /// <summary>bug-5：roots.list 必须声明可选 includeManual（默认只返回 library 根）。</summary>
    [Fact]
    public void RootsListSchema_AdvertisesIncludeManual()
    {
        var schema = OperationSchemas.BuildInputSchema("roots.list");
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("includeManual").GetProperty("type").GetString());
        // 可选参数不进 required；roots.list 全部参数可选时 schema 干脆不含 required 键。
        if (schema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() == "includeManual");
        }
    }

    [Theory]
    [InlineData("games.relink", "newRootPath")]
    [InlineData("profiles.create", "exe")]
    [InlineData("profiles.create", "arg")]
    [InlineData("profiles.update", "exe")]
    [InlineData("profiles.update", "arg")]
    public void ObsoleteCliAlias_IsNotAdvertisedAsWireField(string operationId, string oldField)
    {
        var schema = OperationSchemas.BuildInputSchema(operationId);
        Assert.False(schema.GetProperty("properties").TryGetProperty(oldField, out _));
    }
}
