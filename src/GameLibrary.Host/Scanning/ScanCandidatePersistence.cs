using System.Text.Json;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// 扫描候选落库（T16 统一编排）：手动扫描与周期核对共用——按物理路径 upsert、
/// 抑制规则命中不落库、重扫命中既有 observed 候选晋升 pendingReview（合法双跳）、
/// 事件发布（candidate.discovered / candidate.promoted）。
/// </summary>
public static class ScanCandidatePersistence
{
    public static void Persist(SqliteLibraryStore? store, EventStream? events, ScanCandidateCollector collector, string jobId)
    {
        if (store is null)
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        foreach (var candidate in collector.Candidates)
        {
            if (store.IsSuppressedByIgnoreRule(candidate.PhysicalPath, null))
            {
                continue;
            }

            var existed = store.UpsertCandidate(new PersistedCandidate
            {
                CandidateId = candidate.CandidateId,
                JobId = jobId,
                Kind = ToCamel(candidate.Kind.ToString()),
                RelativePath = candidate.RelativePath,
                PhysicalPath = candidate.PhysicalPath,
                PayloadJson = JsonSerializer.Serialize(candidate.ToDetail(), GameLibrary.Contracts.ContractJson.Options),
                ReviewState = "observed",
                ObservedUtc = candidate.ObservedUtc,
                UpdatedUtc = utcNow,
            });
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
                }, utcNow);
            }
        }
    }

    private static string ToCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
