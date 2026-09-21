using System.Text.Json;
using System.Text.Json.Nodes;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// 扫描候选落库（T16 统一编排）：手动扫描与周期核对共用——按物理路径 upsert、
/// 抑制规则命中不落库；显式手动扫描直接产生 pendingReview，周期核对的新发现先 observed；
/// 重扫命中既有 observed 候选晋升 pendingReview（合法双跳）、
/// 事件发布（candidate.discovered / candidate.promoted）。
/// T17：落库前计算诊断提示（BackupHint/DuplicateHint）写入 payload——只做提示，不自动归类排除。
/// </summary>
public static class ScanCandidatePersistence
{
    /// <summary>备份/副本命名标记：仅提示（补充规格 1.3「目录名含 backup/copy/旧版 → 仅 BackupHint」）。</summary>
    private static readonly string[] BackupMarkers =
    [
        "backup", "copy", "副本", "备份", "旧版",
    ];

    public static void Persist(
        SqliteLibraryStore? store,
        EventStream? events,
        ScanCandidateCollector collector,
        string jobId,
        bool readyForReview = false,
        bool requireRegisteredRoot = false)
    {
        if (store is null)
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        foreach (var candidate in collector.Candidates)
        {
            // 合集是目录结构信息，不是可启动的游戏，不能进入待添加列表。
            if (candidate.Kind == GameLibrary.Domain.States.CandidateKind.Container
                || GameLibrary.Infrastructure.Scanning.DirectoryWalker.IsSystemDirectory(candidate.PhysicalPath))
            {
                continue;
            }

            if (store.IsSuppressedByIgnoreRule(candidate.PhysicalPath, null))
            {
                continue;
            }

            var payloadJson = WithHints(
                store,
                JsonSerializer.Serialize(candidate.ToDetail(), GameLibrary.Contracts.ContractJson.Options),
                candidate.PhysicalPath);

            var persisted = new PersistedCandidate
            {
                CandidateId = candidate.CandidateId,
                JobId = jobId,
                Kind = ToCamel(candidate.Kind.ToString()),
                RelativePath = candidate.RelativePath,
                PhysicalPath = candidate.PhysicalPath,
                PayloadJson = payloadJson,
                ReviewState = readyForReview ? "pendingReview" : "observed",
                ObservedUtc = candidate.ObservedUtc,
                UpdatedUtc = utcNow,
            };
            var stored = requireRegisteredRoot
                ? store.UpsertRegisteredCandidate(persisted)
                : (Stored: true, Existed: store.UpsertCandidate(persisted));
            if (!stored.Stored)
            {
                continue;
            }

            var existed = stored.Existed;
            if (existed)
            {
                store.PromoteRescannedCandidate(candidate.PhysicalPath, utcNow);
                events?.Publish("candidate.promoted", $"candidate:{candidate.PhysicalPath}", new
                {
                    jobId,
                    candidateId = candidate.CandidateId,
                    relativePath = candidate.RelativePath,
                    from = "observed",
                    to = "pendingReview",
                }, utcNow);
            }
            else
            {
                events?.Publish("candidate.discovered", $"candidate:{candidate.PhysicalPath}", new
                {
                    jobId,
                    candidateId = candidate.CandidateId,
                    relativePath = candidate.RelativePath,
                    kind = ToCamel(candidate.Kind.ToString()),
                    reviewState = readyForReview ? "pendingReview" : "observed",
                }, utcNow);
            }
        }

        // T18：候选进入 pendingReview 后汇总为通知批（新候选才触发；ack/defer 的旧批不复活）。
        var notification = store.EnsureCandidateBatch(utcNow);
        if (notification is not null)
        {
            var (batch, created) = notification.Value;
            events?.Publish(
                created ? "notification.created" : "notification.updated",
                $"notification:{batch.NotificationId}",
                new { notificationId = batch.NotificationId, title = batch.Title, count = batch.CandidateIds.Count },
                utcNow);
        }
    }

    /// <summary>
    /// 把诊断提示合并进候选 payload：
    /// - BackupHint：目录名含 backup/copy/副本/备份/旧版 标记；
    /// - DuplicateHint：存在同名标题、不同路径的活动游戏（ID-01/ID-03：是提示，不合并不排除）。
    /// </summary>
    private static string WithHints(SqliteLibraryStore store, string payloadJson, string physicalPath)
    {
        var hints = new List<string>();
        var title = TitleFromPath(physicalPath);

        if (BackupMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            hints.Add("BackupHint");
        }

        var duplicate = store.ListGames()
            .Any(g => string.Equals(g.Membership, "active", StringComparison.Ordinal)
                && !string.Equals(g.RootPath, physicalPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(g.Title, title, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            hints.Add("DuplicateHint");
        }

        if (hints.Count == 0)
        {
            return payloadJson;
        }

        try
        {
            var node = JsonNode.Parse(payloadJson) ?? throw new JsonException("payload 为空");
            node["hints"] = new JsonArray(hints.Select(h => JsonValue.Create(h)).ToArray());
            return node.ToJsonString(new JsonSerializerOptions(GameLibrary.Contracts.ContractJson.Options));
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }

    private static string TitleFromPath(string physicalPath) =>
        Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? physicalPath;

    private static string ToCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
