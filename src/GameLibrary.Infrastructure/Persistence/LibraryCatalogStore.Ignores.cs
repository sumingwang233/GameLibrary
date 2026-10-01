using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

public static partial class LibraryCatalogStore
{
    public static void InsertIgnoreRule(SqliteConnection connection, IgnoreRule rule)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ignore_rules (ignore_id, scope, path, game_id, reason, revision, created_utc)
            VALUES ($id, $scope, $path, $game, $reason, 1, $created)
            """;
        command.Parameters.AddWithValue("$id", rule.IgnoreId);
        command.Parameters.AddWithValue("$scope", rule.Scope);
        command.Parameters.AddWithValue("$path", (object?)rule.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$game", (object?)rule.GameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)rule.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", rule.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<IgnoreRule> ListIgnoreRules(SqliteConnection connection)
    {
        var result = new List<IgnoreRule>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ignore_id, scope, path, game_id, reason, revision, created_utc
            FROM ignore_rules ORDER BY created_utc, ignore_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IgnoreRule
            {
                IgnoreId = reader.GetString(0),
                Scope = reader.GetString(1),
                Path = reader.IsDBNull(2) ? null : reader.GetString(2),
                GameId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Reason = reader.IsDBNull(4) ? null : reader.GetString(4),
                Revision = reader.GetInt32(5),
                CreatedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        }

        return result;
    }

    /// <summary>撤销忽略规则；返回被该规则覆盖且处于 ignored 的候选路径（调用方负责恢复 Observed）。</summary>
    public static IReadOnlyList<string> RemoveIgnoreRule(SqliteConnection connection, string ignoreId)
    {
        var rules = ListIgnoreRules(connection);
        var rule = rules.FirstOrDefault(r => string.Equals(r.IgnoreId, ignoreId, StringComparison.Ordinal));
        if (rule is null)
        {
            return [];
        }

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM ignore_rules WHERE ignore_id = $id";
        delete.Parameters.AddWithValue("$id", ignoreId);
        delete.ExecuteNonQuery();

        if (rule.Scope == "ExactPath" && rule.Path is not null)
        {
            return [rule.Path];
        }

        if (rule.Scope == "Subtree" && rule.Path is not null)
        {
            var covered = new List<string>();
            foreach (var candidate in ListCandidates(connection))
            {
                var candidatePath = candidate.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
                var rulePath = rule.Path.TrimEnd(Path.DirectorySeparatorChar);
                if (candidatePath.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidatePath, rulePath, StringComparison.OrdinalIgnoreCase))
                {
                    covered.Add(candidate.PhysicalPath);
                }
            }

            return covered;
        }

        return [];
    }

    /// <summary>候选抑制判定：ExactPath 同规范路径；Subtree 前缀；ConfirmedIdentity 按 gameId 绑定（随身份证据完善）。</summary>
    public static bool IsSuppressedByIgnoreRule(SqliteConnection connection, string physicalPath, string? boundGameId)
    {
        foreach (var rule in ListIgnoreRules(connection))
        {
            if (rule.Scope == "ExactPath"
                && rule.Path is not null
                && string.Equals(
                    physicalPath.TrimEnd(Path.DirectorySeparatorChar),
                    rule.Path.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (rule.Scope == "Subtree" && rule.Path is not null)
            {
                var candidatePath = physicalPath.TrimEnd(Path.DirectorySeparatorChar);
                var rulePath = rule.Path.TrimEnd(Path.DirectorySeparatorChar);
                if (candidatePath.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (rule.Scope == "ConfirmedIdentity"
                && rule.GameId is not null
                && boundGameId is not null
                && string.Equals(rule.GameId, boundGameId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
