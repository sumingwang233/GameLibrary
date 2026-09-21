using GameLibrary.Domain.States;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Scanning;

public sealed record ReconcileReport(
    int CheckedGames,
    int AvailableCount,
    int SuspectedMissingCount,
    int MissingCount,
    int OfflineCount,
    IReadOnlyList<(string GameId, string From, string To)> Transitions)
{
    public object ToDto() => new
    {
        checkedGames = CheckedGames,
        available = AvailableCount,
        suspectedMissing = SuspectedMissingCount,
        missing = MissingCount,
        offline = OfflineCount,
        transitions = Transitions.Select(t => new { gameId = t.GameId, from = t.From, to = t.To }).ToArray(),
    };
}

/// <summary>
/// 可用性核对（T17，ID-04/ID-05）：
/// - 卷在线（路径盘根存在）但游戏目录缺失 → 第一次 suspectedMissing，两次成功完整核对间隔 ≥60 秒 → missing；
/// - 盘根本身不存在 → offline（保存缺失证据但不累计次数，恢复在线后重新计数）；
/// - offline/accessError/取消/扫描未完成绝不产生 missing（Domain tracker 强制）。
/// 可选补齐 cover 文件；不移动、不改名、不覆盖游戏现有文件。
/// </summary>
public static class ReconcileService
{
    public static ReconcileReport CheckGames(SqliteLibraryStore store, DateTime utcNow, bool synchronizeCovers = true)
    {
        // 旧版已经产生的回收站候选也退出待确认；不移除已由用户接受的游戏。
        foreach (var candidate in store.ListCandidates().Where(candidate =>
            candidate.ReviewState is "observed" or "pendingReview" or "deferred"
            && GameLibrary.Infrastructure.Scanning.DirectoryWalker.IsSystemDirectory(candidate.PhysicalPath)))
        {
            store.TransitionCandidate(candidate.CandidateId, candidate.ReviewState, "ignored", candidate.Revision, null, utcNow);
        }
        foreach (var batch in store.ListNotifications("pending"))
        {
            if (!batch.CandidateIds.Any(id => store.TryGetCandidate(id)?.ReviewState == "pendingReview"))
                store.TransitionNotification(batch.NotificationId, "acknowledged", utcNow);
        }

        int available = 0, suspected = 0, missing = 0, offline = 0;
        var transitions = new List<(string GameId, string From, string To)>();

        foreach (var game in store.ListGames())
        {
            if (!string.Equals(game.Membership, "active", StringComparison.Ordinal))
            {
                continue;
            }

            // 完整扫描和周期核对补齐历史封面，启动快检不执行大批文件复制。
            if (synchronizeCovers) _ = GameCoverService.Synchronize(store, game);

            var rootOnline = DriveRootExists(game.RootPath)
                && (game.Kind != "manualShortcut"
                    || game.EntryPath is not null && DriveRootExists(game.EntryPath));
            var present = rootOnline && (game.Kind switch
            {
                "manualFile" or "fileGame" => File.Exists(game.RootPath),
                "manualShortcut" => File.Exists(game.RootPath)
                    && game.EntryPath is not null && File.Exists(game.EntryPath),
                _ => Directory.Exists(game.RootPath),
            });

            var current = ParseAvailability(game.Availability);
            var evaluation = GameAvailabilityTracker.RecordFullCheck(
                current,
                present,
                rootOnline,
                game.MissingSinceUtc,
                utcNow);

            // 契约：枚举持久化/输出为固定 camelCase 值（available/suspectedMissing/…）。
            var newState = ToCamel(evaluation.NewState.ToString());
            var from = game.Availability;
            if (!string.Equals(from, newState, StringComparison.Ordinal))
            {
                store.UpdateAvailability(game.GameId, newState, evaluation.MissingSinceUtc, utcNow);
                transitions.Add((game.GameId, from, newState));
            }
            else if (evaluation.MissingSinceUtc != game.MissingSinceUtc)
            {
                store.UpdateAvailability(game.GameId, newState, evaluation.MissingSinceUtc, utcNow);
            }

            switch (evaluation.NewState)
            {
                case GameAvailability.Available or GameAvailability.Unknown:
                    available++;
                    break;
                case GameAvailability.SuspectedMissing:
                    suspected++;
                    break;
                case GameAvailability.Missing:
                    missing++;
                    break;
                case GameAvailability.Offline:
                    offline++;
                    break;
            }
        }

        return new ReconcileReport(available + suspected + missing + offline, available, suspected, missing, offline, transitions);
    }

    /// <summary>盘根存在性：F:\Games 缺失但 F:\ 在 → 根在线、目录缺失（可判 suspectedMissing）；F:\ 不在 → 离线。</summary>
    private static bool DriveRootExists(string physicalPath)
    {
        var root = Path.GetPathRoot(physicalPath);
        return root is not null && Directory.Exists(root);
    }

    /// <summary>持久化值为 camelCase（suspectedMissing）；解析忽略大小写并容忍未知值回退 Unknown。</summary>
    private static GameAvailability ParseAvailability(string value) =>
        Enum.TryParse<GameAvailability>(value, ignoreCase: true, out var parsed) ? parsed : GameAvailability.Unknown;

    private static string ToCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
