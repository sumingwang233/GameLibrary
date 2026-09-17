using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using GameLibrary.Host.Hosting;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// T05-A/B 扫描作业执行（宿主内编排；随 T11/T23 重构为 Application 用例 + 端口）。
/// 遍历与覆盖报告 + 目录级检测编排（T05-B）。
/// </summary>
public static class ScanJobRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public static object InitialProgress(GamePath root) => new
    {
        phase = "queued",
        root = root.PhysicalPath,
        completion = (string?)null,
        scannedDirectories = 0L,
        observedFileEntries = 0L,
        candidatesFound = 0,
        currentPath = "",
    };

    public static JobOutcome Run(
        GamePath root,
        JobContext context,
        ScanCandidateCollector? collector = null,
        ScanWalkOptions? options = null,
        Action<ScanCoverageData>? onCompleted = null)
    {
        var walker = new DirectoryWalker(root, new ScanRuleSet([]), options);
        long scannedDirectories = 0;
        long observedFileEntries = 0;
        var currentPath = "";
        var lastReport = System.Diagnostics.Stopwatch.StartNew();

        void ReportProgress(bool force = false)
        {
            if (!force && lastReport.Elapsed < ProgressInterval)
            {
                return;
            }

            context.ReportProgress(new
            {
                phase = "scanning",
                root = root.PhysicalPath,
                completion = (string?)null,
                scannedDirectories,
                observedFileEntries,
                candidatesFound = collector?.CandidateCount ?? 0,
                currentPath,
            });
            lastReport.Restart();
        }

        void ObserveFile(ScannedEntry _)
        {
            observedFileEntries++;
            ReportProgress();
        }

        void InspectDirectory(ScannedDirectory directory)
        {
            scannedDirectories++;
            currentPath = RelativeToRoot(root.PhysicalPath, directory.PhysicalPath);
            ReportProgress(scannedDirectories == 1);
            var before = collector?.CandidateCount ?? 0;
            collector?.InspectDirectory(directory, context.Token);
            if ((collector?.CandidateCount ?? 0) != before)
            {
                ReportProgress(force: true);
            }
        }

        var segments = new List<ScanCoverageData>();
        var coverage = walker.Walk(ObserveFile, pause: null, context.Token, InspectDirectory);
        segments.Add(coverage);
        while (coverage.Completion == ScanCompletion.Partial
            && coverage.UnvisitedBranches > 0
            && coverage.ResumeTokenJson is not null)
        {
            context.Token.ThrowIfCancellationRequested();
            coverage = walker.Resume(
                coverage.ResumeTokenJson,
                ObserveFile,
                pause: null,
                context.Token,
                InspectDirectory);
            segments.Add(coverage);
        }

        collector?.CompleteContainers();
        var aggregate = Aggregate(root.PhysicalPath, segments);
        onCompleted?.Invoke(aggregate);
        context.ReportProgress(ToCoverageDto(aggregate, collector?.CandidateCount ?? 0));
        return aggregate.Completion switch
        {
            ScanCompletion.Cancelled => JobOutcome.Cancelled(),
            _ => JobOutcome.Succeeded(),
        };
    }

    /// <summary>覆盖报告 DTO：lowerCamelCase 字段（契约第 4 节）。</summary>
    public static object ToCoverageDto(ScanCoverageData coverage, int candidatesFound = 0) => new
    {
        phase = "completed",
        root = coverage.Root,
        ruleSetRevision = coverage.RuleSetRevision,
        completion = coverage.Completion.ToString().ToLowerInvariant(),
        scannedDirectories = coverage.ScannedDirectories,
        observedFileEntries = coverage.ObservedFileEntries,
        candidatesFound,
        currentPath = "",
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

    private static string RelativeToRoot(string root, string physicalPath)
    {
        var relative = Path.GetRelativePath(root, physicalPath);
        return relative == "."
            ? ""
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static ScanCoverageData Aggregate(string root, IReadOnlyList<ScanCoverageData> segments)
    {
        var final = segments[^1];
        var excluded = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (ruleId, count) in segments.SelectMany(segment => segment.ExcludedByRule))
        {
            excluded[ruleId] = excluded.GetValueOrDefault(ruleId) + count;
        }

        var cancelled = segments.Any(segment => segment.Completion == ScanCompletion.Cancelled);
        var hasCoverageGaps = segments.Any(segment =>
            segment.Issues.Count > 0 || segment.BudgetDeferred > 0);
        var completion = cancelled
            ? ScanCompletion.Cancelled
            : final.UnvisitedBranches == 0 && !hasCoverageGaps
                ? ScanCompletion.Complete
                : ScanCompletion.Partial;

        return new ScanCoverageData
        {
            Root = root,
            RuleSetRevision = final.RuleSetRevision,
            Completion = completion,
            ScannedDirectories = segments.Sum(segment => segment.ScannedDirectories),
            ObservedFileEntries = segments.Sum(segment => segment.ObservedFileEntries),
            SkippedDirectories = segments.Sum(segment => segment.SkippedDirectories),
            SkippedFiles = segments.Sum(segment => segment.SkippedFiles),
            ExcludedByRule = excluded,
            AccessDenied = segments.Sum(segment => segment.AccessDenied),
            PathTooLong = segments.Sum(segment => segment.PathTooLong),
            ReparsePoints = segments.Sum(segment => segment.ReparsePoints),
            InvalidPaths = segments.Sum(segment => segment.InvalidPaths),
            IoErrors = segments.Sum(segment => segment.IoErrors),
            OfflineRoots = segments.Sum(segment => segment.OfflineRoots),
            BudgetDeferred = segments.Sum(segment => segment.BudgetDeferred),
            UnvisitedBranches = final.UnvisitedBranches,
            ResumeTokenJson = final.ResumeTokenJson,
            Issues = segments.SelectMany(segment => segment.Issues).ToArray(),
        };
    }
}
