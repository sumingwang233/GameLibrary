using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 应用设置（T-settings）：键值存储 + 单调整 Revision。
/// 受限字段集由 Host dispatcher 校验（契约 4：写请求只允许 schema 声明的字段）；
/// 本层只提供读取/写入/重置原语。
/// </summary>
public sealed record AppSettingsSnapshot(
    int Revision,
    string? ActiveViewId,
    bool AutostartEnabled,
    int ScanIntervalMinutes,
    string Theme,
    bool CloseToTray,
    double UiFontScale,
    string UiFontFamily,
    string? CacheParentDirectory)
{
    public const int DefaultScanIntervalMinutes = 15;

    public const string DefaultTheme = "dark";

    public const double DefaultUiFontScale = 1.0;

    public const double MinUiFontScale = 0.85;

    public const double MaxUiFontScale = 1.6;

    public const string DefaultUiFontFamily = "Segoe UI";

    public static AppSettingsSnapshot Defaults(int revision) => new(
        revision,
        ActiveViewId: null,
        AutostartEnabled: false,
        DefaultScanIntervalMinutes,
        DefaultTheme,
        CloseToTray: true,
        DefaultUiFontScale,
        DefaultUiFontFamily,
        CacheParentDirectory: null);
}

public static class SettingsStore
{
    public const string RevisionKey = "__revision";

    /// <summary>读取快照：缺省键回落默认值；损坏值回落默认（不抛出，配置可被 reset 修复）。</summary>
    public static AppSettingsSnapshot Read(SqliteConnection connection)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM app_settings";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var revision = values.TryGetValue(RevisionKey, out var revisionText)
            && int.TryParse(revisionText, out var parsedRevision)
            ? parsedRevision
            : 0;
        return new AppSettingsSnapshot(
            revision,
            ReadString(values, "activeViewId"),
            ReadBool(values, "autostartEnabled", defaultValue: false),
            ReadInt(values, "scanIntervalMinutes", AppSettingsSnapshot.DefaultScanIntervalMinutes),
            ReadString(values, "theme") ?? AppSettingsSnapshot.DefaultTheme,
            ReadBool(values, "closeToTray", defaultValue: true),
            ReadDouble(values, "uiFontScale", AppSettingsSnapshot.DefaultUiFontScale,
                AppSettingsSnapshot.MinUiFontScale, AppSettingsSnapshot.MaxUiFontScale),
            ReadString(values, "uiFontFamily") ?? AppSettingsSnapshot.DefaultUiFontFamily,
            ReadString(values, "cacheParentDirectory"));
    }

    /// <summary>单键写入（事务内递增 Revision）。调用方负责字段校验。</summary>
    public static int WriteKeys(SqliteConnection connection, IEnumerable<(string Key, string? Value)> keys, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        foreach (var (key, value) in keys)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM app_settings WHERE key = $key";
                delete.Parameters.AddWithValue("$key", key);
                delete.ExecuteNonQuery();
            }

            if (value is not null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO app_settings (key, value, updated_utc) VALUES ($key, $value, $now)";
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$value", value);
                insert.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }
        }

        int newRevision;
        using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = """
                INSERT INTO app_settings (key, value, updated_utc) VALUES ($revKey, '1', $now)
                ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), updated_utc = $now
                """;
            bump.Parameters.AddWithValue("$revKey", RevisionKey);
            bump.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            bump.ExecuteNonQuery();
        }

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT value FROM app_settings WHERE key = $revKey";
            select.Parameters.AddWithValue("$revKey", RevisionKey);
            newRevision = int.Parse((string)(select.ExecuteScalar() ?? "0"), CultureInfo.InvariantCulture);
        }

        transaction.Commit();
        return newRevision;
    }

    /// <summary>
    /// 恢复默认：清空全部设置键；Revision 保持单调（读取旧值后 +1，不归零回退——
    /// 归零会让早已缓存的旧 Revision 重新通过乐观校验，破坏单调整承诺）。
    /// </summary>
    public static int ResetAll(SqliteConnection connection, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        long previousRevision;
        using (var readRevision = connection.CreateCommand())
        {
            readRevision.Transaction = transaction;
            readRevision.CommandText = "SELECT value FROM app_settings WHERE key = $key";
            readRevision.Parameters.AddWithValue("$key", RevisionKey);
            var result = readRevision.ExecuteScalar();
            previousRevision = result is string text && long.TryParse(text, out var parsed) ? parsed : 0;
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM app_settings";
            clear.ExecuteNonQuery();
        }

        var newRevision = previousRevision + 1;
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = "INSERT INTO app_settings (key, value, updated_utc) VALUES ($key, $value, $now)";
            seed.Parameters.AddWithValue("$key", RevisionKey);
            seed.Parameters.AddWithValue("$value", newRevision.ToString(CultureInfo.InvariantCulture));
            seed.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            seed.ExecuteNonQuery();
        }

        transaction.Commit();
        return (int)newRevision;
    }

    private static string? ReadString(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static bool ReadBool(Dictionary<string, string> values, string key, bool defaultValue) =>
        values.TryGetValue(key, out var value) ? value == "true" : defaultValue;

    private static int ReadInt(Dictionary<string, string> values, string key, int defaultValue) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;

    private static double ReadDouble(Dictionary<string, string> values, string key, double defaultValue, double min, double max) =>
        values.TryGetValue(key, out var value)
        && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed >= min && parsed <= max
            ? parsed
            : defaultValue;
}
