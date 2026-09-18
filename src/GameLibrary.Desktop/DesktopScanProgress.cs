using System.Text.Json;

namespace GameLibrary.Desktop;

internal static class DesktopScanProgress
{
    public static string Format(JsonElement coverage, int rootIndex, int rootCount)
    {
        var phase = StringValue(coverage, "phase");
        if (phase == "queued")
        {
            return $"准备扫描（根 {rootIndex}/{rootCount}）";
        }

        var directories = Int64Value(coverage, "scannedDirectories");
        var files = Int64Value(coverage, "observedFileEntries");
        var candidates = Int64Value(coverage, "candidatesFound");
        var currentPath = StringValue(coverage, "currentPath");
        var text = $"扫描（根 {rootIndex}/{rootCount}）已检查 {directories:N0} 个目录、{files:N0} 个文件，识别 {candidates:N0} 个候选（含已入库）";
        return string.IsNullOrWhiteSpace(currentPath)
            ? text
            : $"{text} · 当前：{currentPath}";
    }

    private static long Int64Value(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
            ? property.GetInt64()
            : 0;

    private static string? StringValue(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
