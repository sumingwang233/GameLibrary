using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using GameLibrary.Host.Hosting;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// T05-A 扫描作业执行（宿主内编排；随 T11/T23 重构为 Application 用例 + 端口）。
/// 仅遍历与覆盖报告；检测与候选编排在 T05-B 进入。
/// </summary>
public static class ScanJobRunner
{
    public static JobOutcome Run(GamePath root, JobContext context)
    {
        var walker = new DirectoryWalker(root, new ScanRuleSet([]));
        long scannedEntries = 0;

        var coverage = walker.Walk(
            _ =>
            {
                var total = Interlocked.Increment(ref scannedEntries);
                if (total % 64 == 0)
                {
                    context.ReportProgress(new { scannedEntries = total });
                }
            },
            pause: null,
            context.Token);

        context.ReportProgress(ToCoverageDto(coverage));
        return coverage.Completion switch
        {
            ScanCompletion.Cancelled => JobOutcome.Cancelled(),
            _ => JobOutcome.Succeeded(),
        };
    }

    /// <summary>覆盖报告 DTO：lowerCamelCase 字段（契约第 4 节）。</summary>
    public static object ToCoverageDto(ScanCoverageData coverage) => new
    {
        root = coverage.Root,
        ruleSetRevision = coverage.RuleSetRevision,
        completion = coverage.Completion.ToString().ToLowerInvariant(),
        scannedDirectories = coverage.ScannedDirectories,
        observedFileEntries = coverage.ObservedFileEntries,
        skippedDirectories = coverage.SkippedDirectories,
        skippedFiles = coverage.SkippedFiles,
        excludedByRule = coverage.ExcludedByRule,
        accessDenied = coverage.AccessDenied,
        pathTooLong = coverage.PathTooLong,
        reparsePoints = coverage.ReparsePoints,
        invalidPaths = coverage.InvalidPaths,
        ioErrors = coverage.IoErrors,
        offlineRoots = coverage.OfflineRoots,
        budgetDeferred = coverage.BudgetDeferred,
        unvisitedBranches = coverage.UnvisitedBranches,
        resumeToken = coverage.ResumeTokenJson,
        issues = coverage.Issues.Select(i => new
        {
            path = i.Path,
            classification = i.Classification,
            retryable = i.Retryable,
            detail = i.Detail,
        }).ToArray(),
    };
}
