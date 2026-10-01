using System.Text.Json;
using GameLibrary.Application.Catalog;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Infrastructure.Catalog;

public sealed class CatalogFiles : ICatalogFiles
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public GameFingerprintData? Fingerprint(string path, string? entryPath, string? engine, DateTime utcNow)
    {
        var fingerprint = MatchFingerprintCalculator.Calculate(path, entryPath, engine);
        return fingerprint is null ? null : new(fingerprint.StrategyVersion,
            JsonSerializer.Serialize(fingerprint.Entries, new JsonSerializerOptions(JsonSerializerDefaults.Web)), utcNow);
    }
}
