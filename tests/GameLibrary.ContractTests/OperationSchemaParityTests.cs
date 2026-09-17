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
