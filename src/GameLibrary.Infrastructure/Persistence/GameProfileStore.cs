using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>字段层：value 为 null 表示用户主动清空（clear ≠ 恢复自动值）。</summary>
public sealed record GameFieldValue
{
    public required string GameId { get; init; }

    public required string FieldKey { get; init; }

    public string? Value { get; init; }

    public required string Source { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

public sealed record GameAsset
{
    public required string AssetId { get; init; }

    public required string GameId { get; init; }

    public required string Kind { get; init; }

    public required string FilePath { get; init; }

    public bool IsCurrent { get; init; }

    public required DateTime ImportedUtc { get; init; }
}

/// <summary>
/// 资料与封面存储（T14）：字段分层（user 覆盖 auto），Revision 即 games.revision（乐观校验）；
/// 记录应用自有目录（assets/）中的封面；游戏目录 cover 同步由 Host 单独编排。
/// </summary>
public static class GameProfileStore
{
    public static GameFieldValue? TryGetField(SqliteConnection connection, string gameId, string fieldKey)
    {
        // 分层主键 (game_id, field_key, source)：同一字段可同时存在 auto/user 两行；
        // 读取按生效序（user 优先，value 非 null 优先）。
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value, source, updated_utc
            FROM game_fields WHERE game_id = $game AND field_key = $key
            ORDER BY CASE source WHEN 'user' THEN 0 ELSE 1 END,
                     CASE WHEN value IS NULL THEN 1 ELSE 0 END
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$key", fieldKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GameFieldValue
        {
            GameId = gameId,
            FieldKey = fieldKey,
            Value = reader.IsDBNull(0) ? null : reader.GetString(0),
            Source = reader.GetString(1),
            UpdatedUtc = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    public static void UpsertField(SqliteConnection connection, GameFieldValue field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_fields (game_id, field_key, value, source, revision, updated_utc)
            VALUES ($game, $key, $value, $source, 1, $updated)
            ON CONFLICT(game_id, field_key, source) DO UPDATE SET
                value = excluded.value,
                revision = revision + 1,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$game", field.GameId);
        command.Parameters.AddWithValue("$key", field.FieldKey);
        command.Parameters.AddWithValue("$value", (object?)field.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", field.Source);
        command.Parameters.AddWithValue("$updated", field.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void DeleteField(SqliteConnection connection, string gameId, string fieldKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_fields WHERE game_id = $game AND field_key = $key";
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$key", fieldKey);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 资料字段变更：期望 Revision 对齐 games.revision，事务内更新字段层并镜像
    /// games.title（列表标题即生效值）、递增 games.revision；冲突返回 null。
    /// 首次覆盖前先把当前生效值登记为 auto 层（供 reset 恢复）。
    /// </summary>
    public static int? SetGameField(
        SqliteConnection connection,
        string gameId,
        string fieldKey,
        string? value,
        string source,
        int expectedRevision,
        DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        int currentRevision;
        string currentTitle;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision, title FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return null;
            }

            currentRevision = reader.GetInt32(0);
            currentTitle = reader.GetString(1);
        }

        if (currentRevision != expectedRevision)
        {
            transaction.Rollback();
            return null;
        }

        // 自动层登记：仅当该字段从未有任何层记录时，把当前生效值存为 auto。
        EnsureAutoRowWithTransaction(connection, transaction, gameId, fieldKey, currentTitle);

        UpsertFieldWithTransaction(connection, transaction, new GameFieldValue
        {
            GameId = gameId,
            FieldKey = fieldKey,
            Value = value,
            Source = source,
            UpdatedUtc = utcNow,
        });

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE games SET revision = revision + 1, updated_utc = $now" +
                (fieldKey == "title" ? ", title = $title" : "") +
                " WHERE game_id = $game";
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$title", value ?? "");
            update.Parameters.AddWithValue("$game", gameId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return currentRevision + 1;
    }

    /// <summary>
    /// 恢复自动值（fields.reset）：删除用户覆盖行；title 镜像取自动层值
    /// （无自动层记录时回退 fallback，如根目录名）；冲突返回 null。
    /// </summary>
    public static int? ResetGameField(
        SqliteConnection connection,
        string gameId,
        string fieldKey,
        string fallbackValue,
        int expectedRevision,
        DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        int currentRevision;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            var result = select.ExecuteScalar();
            if (result is null)
            {
                transaction.Rollback();
                return null;
            }

            currentRevision = Convert.ToInt32(result);
        }

        if (currentRevision != expectedRevision)
        {
            transaction.Rollback();
            return null;
        }

        string autoValue = fallbackValue;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT value FROM game_fields
                WHERE game_id = $game AND field_key = $key AND source = 'auto'
                """;
            select.Parameters.AddWithValue("$game", gameId);
            select.Parameters.AddWithValue("$key", fieldKey);
            var result = select.ExecuteScalar();
            if (result is not null && result != DBNull.Value)
            {
                autoValue = (string)result;
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM game_fields WHERE game_id = $game AND field_key = $key AND source = 'user'";
            delete.Parameters.AddWithValue("$game", gameId);
            delete.Parameters.AddWithValue("$key", fieldKey);
            delete.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE games SET revision = revision + 1, updated_utc = $now" +
                (fieldKey == "title" ? ", title = $title" : "") +
                " WHERE game_id = $game";
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$title", autoValue);
            update.Parameters.AddWithValue("$game", gameId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return currentRevision + 1;
    }

    public static void WriteAutoField(SqliteConnection connection, string gameId, string fieldKey, string value, DateTime utcNow)
    {
        // 元数据刷新只更新 auto 层；user 覆盖行不受影响（分层主键下天然隔离）。
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_fields (game_id, field_key, value, source, revision, updated_utc)
            VALUES ($game, $key, $value, 'auto', 1, $updated)
            ON CONFLICT(game_id, field_key, source) DO UPDATE SET
                value = excluded.value,
                revision = revision + 1,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$key", fieldKey);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void EnsureAutoRowWithTransaction(
        SqliteConnection connection, SqliteTransaction transaction, string gameId, string fieldKey, string currentTitle)
    {
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = """
            SELECT COUNT(1) FROM game_fields
            WHERE game_id = $game AND field_key = $key AND source = 'auto'
            """;
        exists.Parameters.AddWithValue("$game", gameId);
        exists.Parameters.AddWithValue("$key", fieldKey);
        if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
        {
            return;
        }

        var autoValue = fieldKey == "title" ? currentTitle : "";
        UpsertFieldWithTransaction(connection, transaction, new GameFieldValue
        {
            GameId = gameId,
            FieldKey = fieldKey,
            Value = autoValue,
            Source = "auto",
            UpdatedUtc = DateTime.UtcNow,
        });
    }

    private static void UpsertFieldWithTransaction(
        SqliteConnection connection, SqliteTransaction transaction, GameFieldValue field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_fields (game_id, field_key, value, source, revision, updated_utc)
            VALUES ($game, $key, $value, $source, 1, $updated)
            ON CONFLICT(game_id, field_key, source) DO UPDATE SET
                value = excluded.value,
                revision = revision + 1,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$game", field.GameId);
        command.Parameters.AddWithValue("$key", field.FieldKey);
        command.Parameters.AddWithValue("$value", (object?)field.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", field.Source);
        command.Parameters.AddWithValue("$updated", field.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static GameAsset ImportAsset(SqliteConnection connection, string gameId, string importedFilePath, DateTime utcNow)
    {
        using var unset = connection.CreateCommand();
        unset.CommandText = "UPDATE game_assets SET is_current = 0 WHERE game_id = $game AND kind = 'cover'";
        unset.Parameters.AddWithValue("$game", gameId);
        unset.ExecuteNonQuery();

        var assetId = $"asset-{Guid.NewGuid():N}";
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_assets (asset_id, game_id, kind, file_path, is_current, imported_utc)
            VALUES ($id, $game, 'cover', $path, 1, $imported)
            """;
        command.Parameters.AddWithValue("$id", assetId);
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$path", importedFilePath);
        command.Parameters.AddWithValue("$imported", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return new GameAsset
        {
            AssetId = assetId,
            GameId = gameId,
            Kind = "cover",
            FilePath = importedFilePath,
            IsCurrent = true,
            ImportedUtc = utcNow,
        };
    }

    public static IReadOnlyList<GameAsset> ListAssets(SqliteConnection connection, string gameId)
    {
        var result = new List<GameAsset>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT asset_id, game_id, kind, file_path, is_current, imported_utc
            FROM game_assets WHERE game_id = $game ORDER BY imported_utc, asset_id
            """;
        command.Parameters.AddWithValue("$game", gameId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GameAsset
            {
                AssetId = reader.GetString(0),
                GameId = reader.GetString(1),
                Kind = reader.GetString(2),
                FilePath = reader.GetString(3),
                IsCurrent = reader.GetInt64(4) == 1,
                ImportedUtc = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        }

        return result;
    }

    public static GameAsset? TryGetAsset(SqliteConnection connection, string assetId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT asset_id, game_id, kind, file_path, is_current, imported_utc
            FROM game_assets WHERE asset_id = $id
            """;
        command.Parameters.AddWithValue("$id", assetId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GameAsset
        {
            AssetId = reader.GetString(0),
            GameId = reader.GetString(1),
            Kind = reader.GetString(2),
            FilePath = reader.GetString(3),
            IsCurrent = reader.GetInt64(4) == 1,
            ImportedUtc = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    /// <summary>选择某资产为当前封面（assets.choose）：先清其余 current。</summary>
    public static void ChooseAsset(SqliteConnection connection, string gameId, string assetId)
    {
        using var unset = connection.CreateCommand();
        unset.CommandText = "UPDATE game_assets SET is_current = 0 WHERE game_id = $game AND kind = 'cover'";
        unset.Parameters.AddWithValue("$game", gameId);
        unset.ExecuteNonQuery();

        using var set = connection.CreateCommand();
        set.CommandText = "UPDATE game_assets SET is_current = 1 WHERE asset_id = $id";
        set.Parameters.AddWithValue("$id", assetId);
        set.ExecuteNonQuery();
    }

    /// <summary>重置封面（assets.reset）：全部置为非当前；返回此前 current 的 assetId。</summary>
    public static string? ResetCover(SqliteConnection connection, string gameId)
    {
        var current = ListAssets(connection, gameId).FirstOrDefault(a => a.IsCurrent);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE game_assets SET is_current = 0 WHERE game_id = $game AND kind = 'cover'";
        command.Parameters.AddWithValue("$game", gameId);
        command.ExecuteNonQuery();
        return current?.AssetId;
    }

    /// <summary>
    /// 移除资产（assets.remove）：仅限应用自有且非当前引用的资源；
    /// 返回被删除的文件路径（调用方删文件），资产不存在或被引用返回 null。
    /// </summary>
    public static string? RemoveAsset(SqliteConnection connection, string assetId)
    {
        var asset = TryGetAsset(connection, assetId);
        if (asset is null || asset.IsCurrent)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_assets WHERE asset_id = $id";
        command.Parameters.AddWithValue("$id", assetId);
        command.ExecuteNonQuery();
        return asset.FilePath;
    }

    /// <summary>当前生效字段值：用户层优先（value 可为 null=清空），否则自动层，否则回退值。</summary>
    public static (string? Value, string Source) EffectiveField(
        SqliteConnection connection, string gameId, string fieldKey, string fallback)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value, source FROM game_fields
            WHERE game_id = $game AND field_key = $key
            ORDER BY CASE source WHEN 'user' THEN 0 ELSE 1 END
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$key", fieldKey);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1));
        }

        return (fallback, "auto");
    }
}
