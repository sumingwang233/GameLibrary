using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Domain.Identity;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>similarTo 建议条目（候选审核响应 / games.get 详情 / game.created 事件共用形状）。</summary>
public sealed record SimilarGameSuggestion(string GameId, string Title, double Similarity);

/// <summary>
/// 相似建议门控（宿主建议层参数，不属指纹语义、不动 StrategyVersion）：
/// similarity ≥ 0.6 且 matchedCount ≥ 2（matched 口径 = 双侧 Sha256 非空条目中同键同哈希的对数，
/// 与 Domain SimilarityWith 的字典交集同构复算）。matched≥2 拦单条目巧合——两游戏共用同一
/// 启动器/壳 exe 时 matched=1 被拦。排除自身 gameId；相似度降序、≤5 条；无命中时空数组。
/// </summary>
internal static class FingerprintSuggestions
{
    internal const double SimilarityThreshold = 0.6;
    internal const int MatchedCountThreshold = 2;
    internal const int MaxSuggestions = 5;

    internal static IReadOnlyList<SimilarGameSuggestion> Compute(
        SqliteLibraryStore store,
        MatchFingerprint fingerprint,
        string excludeGameId)
    {
        var mine = fingerprint.Entries
            .Where(e => e.Sha256 is not null)
            .GroupBy(e => e.RelativeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Sha256, StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0)
        {
            return [];
        }

        var rows = store.ListActiveGameFingerprints(excludeGameId, fingerprint.StrategyVersion);
        var candidates = new List<(string GameId, string Title, double Similarity)>();
        foreach (var row in rows)
        {
            MatchFingerprint? other;
            try
            {
                var entries = JsonSerializer.Deserialize<IReadOnlyList<MatchFingerprint.FingerprintEntry>>(
                    row.EntriesJson, ContractJson.Options);
                other = entries is null
                    ? null
                    : new MatchFingerprint { StrategyVersion = row.StrategyVersion, Entries = entries };
            }
            catch (JsonException)
            {
                continue;
            }

            if (other is null)
            {
                continue;
            }

            var similarity = fingerprint.SimilarityWith(other);
            if (similarity < SimilarityThreshold)
            {
                continue;
            }

            // 与 SimilarityWith 同构的字典交集计数：仅 Sha256 非空条目参与（缺失/只记大小的条目不计）。
            var matched = other.Entries
                .Where(e => e.Sha256 is not null)
                .Count(e => mine.TryGetValue(e.RelativeKey, out var hash)
                    && string.Equals(hash, e.Sha256, StringComparison.Ordinal));
            if (matched < MatchedCountThreshold)
            {
                continue;
            }

            candidates.Add((row.GameId, row.Title ?? row.GameId, similarity));
        }

        return candidates
            .OrderByDescending(c => c.Similarity)
            .ThenBy(c => c.GameId, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(c => new SimilarGameSuggestion(c.GameId, c.Title, c.Similarity))
            .ToArray();
    }

    internal static object[] ToPayload(IReadOnlyList<SimilarGameSuggestion> suggestions) =>
        suggestions
            .Select(s => (object)new { s.GameId, s.Title, s.Similarity })
            .ToArray();
}
