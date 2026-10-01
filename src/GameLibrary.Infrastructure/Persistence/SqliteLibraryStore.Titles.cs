using System.Globalization;
using GameLibrary.Domain.Catalog;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

public sealed partial class SqliteLibraryStore
{
    public GameTitleTranslation? ReadTitleTranslation(string gameId) => ReadExclusive((connection, _) =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT translated_title, source_title, provider, translated_utc, manually_edited, display_mode FROM game_title_translations WHERE game_id = $id";
        command.Parameters.AddWithValue("$id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTitleTranslationRow(reader) : null;
    });

    internal static GameTitleTranslation ReadTitleTranslationRow(SqliteDataReader reader, int offset = 0) => new(
        reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3),
        reader.GetInt32(offset + 4) != 0, reader.GetString(offset + 5));

    /// <summary>CAS protects the original title and user edits made while HTTP was in flight.</summary>
    public int? SaveTitleTranslation(string gameId, string originalTitle, string translatedTitle,
        string provider, int expectedRevision, DateTime utcNow) => Execute((connection, _) =>
    {
        using var owned = _writeTransaction is null ? connection.BeginTransaction() : null;
        var transaction = _writeTransaction ?? owned!;
        var effective = GameProfileStore.EffectiveField(connection, gameId, "title", TryGetGame(gameId)?.Title ?? "").Value ?? "";
        if (!string.Equals(effective, originalTitle, StringComparison.Ordinal)) return null;
        if (!BumpTitleRevision(connection, transaction, gameId, expectedRevision, utcNow)) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_title_translations(game_id, translated_title, source_title, provider, translated_utc, manually_edited, display_mode)
            VALUES($id, $title, $source, $provider, $now, 0, 'translated')
            ON CONFLICT(game_id) DO UPDATE SET translated_title = excluded.translated_title,
                source_title = excluded.source_title, provider = excluded.provider, translated_utc = excluded.translated_utc,
                manually_edited = 0, display_mode = 'translated'
            """;
        command.Parameters.AddWithValue("$id", gameId);
        command.Parameters.AddWithValue("$title", translatedTitle);
        command.Parameters.AddWithValue("$source", originalTitle);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        owned?.Commit();
        return (int?)(expectedRevision + 1);
    });

    public int? EditTitleTranslation(string gameId, string? title, string? displayMode,
        int expectedRevision, DateTime utcNow) => Execute((connection, _) =>
    {
        using var owned = _writeTransaction is null ? connection.BeginTransaction() : null;
        var transaction = _writeTransaction ?? owned!;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM game_title_translations WHERE game_id = $id";
        command.Parameters.AddWithValue("$id", gameId);
        if (command.ExecuteScalar() is null || !BumpTitleRevision(connection, transaction, gameId, expectedRevision, utcNow)) return null;
        command.CommandText = title is not null
            ? "UPDATE game_title_translations SET translated_title = $value, manually_edited = 1 WHERE game_id = $id"
            : "UPDATE game_title_translations SET display_mode = $value WHERE game_id = $id";
        command.Parameters.AddWithValue("$value", title ?? displayMode!);
        command.ExecuteNonQuery();
        owned?.Commit();
        return (int?)(expectedRevision + 1);
    });

    private static bool BumpTitleRevision(SqliteConnection connection, SqliteTransaction transaction,
        string gameId, int revision, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE games SET revision = revision + 1, updated_utc = $now WHERE game_id = $id AND revision = $revision AND membership = 'active'";
        command.Parameters.AddWithValue("$id", gameId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1;
    }
}
