using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

public static partial class LibraryCatalogStore
{
    /// <summary>指纹行 upsert（INSERT OR REPLACE）：accept 经 R43 事务内调用；relink/create 在各自提交后调用。</summary>
    public static void UpsertGameFingerprint(
        SqliteConnection connection,
        string gameId,
        GameFingerprintData fingerprint,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText = """
            INSERT OR REPLACE INTO game_fingerprints (game_id, strategy_version, entries_json, computed_utc)
            VALUES ($game, $version, $entries, $computed)
            """;
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$version", fingerprint.StrategyVersion);
        command.Parameters.AddWithValue("$entries", fingerprint.EntriesJson);
        command.Parameters.AddWithValue("$computed", fingerprint.ComputedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static GameFingerprintRow? TryGetGameFingerprint(SqliteConnection connection, string gameId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, strategy_version, entries_json, computed_utc
            FROM game_fingerprints WHERE game_id = $id
            """;
        command.Parameters.AddWithValue("$id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFingerprint(reader) : null;
    }

    /// <summary>
    /// 相似建议对比集合：active 游戏的当前策略版本指纹（JOIN games membership='active'）。
    /// excludeGameId 防呆——刚 accept/relink 的游戏自身 Membership='active' 且指纹行已在同事务插入，
    /// 不排除则 SimilarityWith(自身)=1.0 恒占 similarTo[0]。
    /// </summary>
    public static IReadOnlyList<GameFingerprintRow> ListActiveGameFingerprints(
        SqliteConnection connection,
        string? excludeGameId,
        int strategyVersion)
    {
        var result = new List<GameFingerprintRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.game_id, f.strategy_version, f.entries_json, f.computed_utc, g.title
            FROM game_fingerprints f
            JOIN games g ON g.game_id = f.game_id
            WHERE g.membership = 'active'
              AND f.strategy_version = $version
              AND ($exclude IS NULL OR f.game_id <> $exclude)
            ORDER BY f.game_id
            """;
        command.Parameters.AddWithValue("$exclude", (object?)excludeGameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", strategyVersion);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadFingerprint(reader) with { Title = reader.GetString(4) });
        }

        return result;
    }

    private static GameFingerprintRow ReadFingerprint(SqliteDataReader reader) => new()
    {
        GameId = reader.GetString(0),
        StrategyVersion = reader.GetInt32(1),
        EntriesJson = reader.GetString(2),
        ComputedUtc = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
