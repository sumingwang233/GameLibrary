using System.Text.Json;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;

namespace GameLibrary.Infrastructure.Scanning;

public enum ScanCompletion
{
    Complete,

    Partial,

    Cancelled,
}

/// <summary>枚举预算与深度限制（策划案 5.2）；达到任一上限即保存续扫游标返回 partial。</summary>
public sealed record ScanWalkOptions
{
    public int MaxDepth { get; init; } = 12;

    public int MaxDirectories { get; init; } = 20_000;

    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>枚举产出条目；规则结论随条目流动（Exclude 的条目不产出，只计数）。</summary>
public sealed record ScannedEntry(
    string PhysicalPath,
    string ComparisonKey,
    bool IsDirectory,
    int Depth,
    ScanRuleOutcome RuleOutcome,
    string? WinningRuleId);

/// <summary>分支问题：路径、分类、可重试性（补充规格 2.2）。</summary>
public sealed record ScanBranchIssue(string Path, string Classification, bool Retryable, string? Detail);

/// <summary>覆盖报告（补充规格 2.2 的 Walker 层子集；汇总与分页属 ScanCoordinator/T16）。</summary>
public sealed record ScanCoverageData
{
    public required string Root { get; init; }

    public int RuleSetRevision { get; init; }

    public required ScanCompletion Completion { get; init; }

    public long ScannedDirectories { get; init; }

    public long ObservedFileEntries { get; init; }

    public long SkippedDirectories { get; init; }

    public long SkippedFiles { get; init; }

    public required IReadOnlyDictionary<string, long> ExcludedByRule { get; init; }

    public long AccessDenied { get; init; }

    public long PathTooLong { get; init; }

    public long ReparsePoints { get; init; }

    public long InvalidPaths { get; init; }

    public long IoErrors { get; init; }

    public long OfflineRoots { get; init; }

    /// <summary>深度上限或预算延后的分支。</summary>
    public long BudgetDeferred { get; init; }

    public long UnvisitedBranches { get; init; }

    public string? ResumeTokenJson { get; init; }

    public IReadOnlyList<ScanBranchIssue> Issues { get; init; } = [];
}

/// <summary>暂停信号：Walker 在目录间安全检查点等待，恢复后继续。</summary>
public sealed class PauseSignal
{
    private readonly ManualResetEventSlim _running = new(true);

    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    public void WaitWhilePaused(CancellationToken ct) => _running.Wait(ct);
}

/// <summary>
/// 只读目录遍历器（ADR-0002）：显式栈 DFS、分段检查取消/暂停/预算、重解析点不跟随、
/// 目录规则 Exclude 剪枝整棵子树并计数。本类不读文件内容、不做识别。
/// </summary>
public sealed class DirectoryWalker
{
    private readonly GamePath _root;
    private readonly ScanRuleSet _rules;
    private readonly ScanWalkOptions _options;

    public DirectoryWalker(GamePath root, ScanRuleSet rules, ScanWalkOptions? options = null)
    {
        _root = root;
        _rules = rules;
        _options = options ?? new ScanWalkOptions();
    }

    public ScanCoverageData Walk(
        Action<ScannedEntry> sink,
        PauseSignal? pause,
        CancellationToken ct,
        Action<string>? onDirectory = null) =>
        WalkCore(stackRoots: null, sink, pause, ct, onDirectory);

    /// <summary>从上次 partial 的游标续扫；游标与根不匹配时抛 <see cref="InvalidOperationException"/>。</summary>
    public ScanCoverageData Resume(
        string resumeTokenJson,
        Action<ScannedEntry> sink,
        PauseSignal? pause,
        CancellationToken ct,
        Action<string>? onDirectory = null)
    {
        var state = JsonSerializer.Deserialize<WalkResumeState>(resumeTokenJson)
            ?? throw new InvalidOperationException("续扫游标损坏");
        if (!string.Equals(state.RootComparisonKey, _root.ComparisonKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"续扫游标属于其他根：{state.RootComparisonKey}");
        }

        return WalkCore([.. state.RemainingDirectories], sink, pause, ct, onDirectory);
    }

    private ScanCoverageData WalkCore(
        List<string>? stackRoots,
        Action<ScannedEntry> sink,
        PauseSignal? pause,
        CancellationToken ct,
        Action<string>? onDirectory)
    {
        long scannedDirectories = 0;
        long observedFiles = 0;
        long skippedDirectories = 0;
        long skippedFiles = 0;
        long accessDenied = 0;
        long pathTooLong = 0;
        long reparsePoints = 0;
        long invalidPaths = 0;
        long ioErrors = 0;
        long offlineRoots = 0;
        long budgetDeferred = 0;
        var excludedByRule = new Dictionary<string, long>(StringComparer.Ordinal);
        var issues = new List<ScanBranchIssue>();

        var stack = stackRoots is not null
            // 序列化顺序是栈顶优先；Stack 构造按序压入，需反转以保持弹出顺序。
            ? new Stack<(string Path, int Depth)>(stackRoots.Select(p => (p, DepthFromRoot(p))).Reverse())
            : new Stack<(string Path, int Depth)>();
        if (stackRoots is null)
        {
            stack.Push((_root.PhysicalPath, 0));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ScanCompletion completion = ScanCompletion.Complete;

        try
        {
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                pause?.WaitWhilePaused(ct);

                if (scannedDirectories >= _options.MaxDirectories || stopwatch.Elapsed >= _options.TimeBudget)
                {
                    completion = ScanCompletion.Partial;
                    break;
                }

                var (dirPath, depth) = stack.Pop();
                var enumerateStatus = TryEnumerate(dirPath, out var subDirs, out var files);
                if (enumerateStatus != EnumerateStatus.Ok)
                {
                    switch (enumerateStatus)
                    {
                        case EnumerateStatus.AccessDenied:
                            accessDenied++;
                            issues.Add(new ScanBranchIssue(dirPath, "AccessDenied", Retryable: true, null));
                            break;
                        case EnumerateStatus.PathTooLong:
                            pathTooLong++;
                            issues.Add(new ScanBranchIssue(dirPath, "PathTooLong", Retryable: true, null));
                            break;
                        case EnumerateStatus.DirectoryMissing when depth == 0:
                            offlineRoots++;
                            issues.Add(new ScanBranchIssue(dirPath, "OfflineRoot", Retryable: true, null));
                            break;
                        case EnumerateStatus.DirectoryMissing:
                            ioErrors++;
                            issues.Add(new ScanBranchIssue(dirPath, "DirectoryMissing", Retryable: true, null));
                            break;
                        default:
                            ioErrors++;
                            issues.Add(new ScanBranchIssue(dirPath, "IoError", Retryable: true, null));
                            break;
                    }

                    continue;
                }

                scannedDirectories++;
                onDirectory?.Invoke(dirPath);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TryCreatePath(file, depth, out var filePath))
                    {
                        invalidPaths++;
                        continue;
                    }

                    var decision = _rules.Decide(filePath);
                    if (decision.Outcome == ScanRuleOutcome.Exclude)
                    {
                        skippedFiles++;
                        CountExclusion(excludedByRule, decision.WinningRuleId!);
                        continue;
                    }

                    observedFiles++;
                    sink(new ScannedEntry(filePath.PhysicalPath, filePath.ComparisonKey, IsDirectory: false, depth, decision.Outcome, decision.WinningRuleId));
                }

                foreach (var subDir in subDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TryCreatePath(subDir, depth, out var dirPathValue))
                    {
                        invalidPaths++;
                        continue;
                    }

                    if (HasReparsePoint(subDir))
                    {
                        reparsePoints++;
                        continue;
                    }

                    var decision = _rules.Decide(dirPathValue);
                    if (decision.Outcome == ScanRuleOutcome.Exclude)
                    {
                        skippedDirectories++;
                        CountExclusion(excludedByRule, decision.WinningRuleId!);
                        continue;
                    }

                    if (depth + 1 > _options.MaxDepth)
                    {
                        budgetDeferred++;
                        issues.Add(new ScanBranchIssue(dirPathValue.PhysicalPath, "DepthLimit", Retryable: true, $"深度 {depth + 1} > {_options.MaxDepth}"));
                        continue;
                    }

                    stack.Push((dirPathValue.PhysicalPath, depth + 1));
                }
            }
        }
        catch (OperationCanceledException)
        {
            completion = ScanCompletion.Cancelled;
        }

        var remaining = stack.Select(item => item.Path).ToList();
        if (completion == ScanCompletion.Complete && issues.Count > 0)
        {
            // 分支失败/访问拒绝/离线根：覆盖有缺口，不能报告 complete。
            completion = ScanCompletion.Partial;
        }

        string? resumeToken = null;
        if (completion == ScanCompletion.Partial)
        {
            resumeToken = JsonSerializer.Serialize(new WalkResumeState
            {
                RootComparisonKey = _root.ComparisonKey,
                RemainingDirectories = remaining,
            });
        }

        return new ScanCoverageData
        {
            Root = _root.PhysicalPath,
            RuleSetRevision = _rules.RuleSetRevision,
            Completion = completion,
            ScannedDirectories = scannedDirectories,
            ObservedFileEntries = observedFiles,
            SkippedDirectories = skippedDirectories,
            SkippedFiles = skippedFiles,
            ExcludedByRule = excludedByRule,
            AccessDenied = accessDenied,
            PathTooLong = pathTooLong,
            ReparsePoints = reparsePoints,
            InvalidPaths = invalidPaths,
            IoErrors = ioErrors,
            OfflineRoots = offlineRoots,
            BudgetDeferred = budgetDeferred,
            UnvisitedBranches = remaining.Count,
            ResumeTokenJson = resumeToken,
            Issues = issues,
        };
    }

    private int DepthFromRoot(string path)
    {
        var gamePath = GamePath.Create(path);
        return gamePath.Segments.Count - _root.Segments.Count;
    }

    private static EnumerateStatus TryEnumerate(string dir, out IReadOnlyList<string> subDirs, out IReadOnlyList<string> files)
    {
        subDirs = [];
        files = [];
        try
        {
            subDirs = [.. Directory.EnumerateDirectories(dir)];
            files = [.. Directory.EnumerateFiles(dir)];
            return EnumerateStatus.Ok;
        }
        catch (UnauthorizedAccessException)
        {
            return EnumerateStatus.AccessDenied;
        }
        catch (PathTooLongException)
        {
            return EnumerateStatus.PathTooLong;
        }
        catch (IOException)
        {
            return Directory.Exists(dir)
                ? EnumerateStatus.IoError
                : EnumerateStatus.DirectoryMissing;
        }
    }

    private enum EnumerateStatus
    {
        Ok,

        AccessDenied,

        PathTooLong,

        IoError,

        DirectoryMissing,
    }

    private static bool TryCreatePath(string path, int depth, out GamePath gamePath)
    {
        var validation = GamePath.TryCreate(path);
        if (validation.IsValid)
        {
            gamePath = validation.Path!;
            return true;
        }

        gamePath = null!;
        return false;
    }

    private static bool HasReparsePoint(string directoryPath)
    {
        try
        {
            return (new DirectoryInfo(directoryPath).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CountExclusion(Dictionary<string, long> counts, string ruleId)
    {
        counts.TryGetValue(ruleId, out var current);
        counts[ruleId] = current + 1;
    }

    private sealed record WalkResumeState
    {
        public required string RootComparisonKey { get; init; }

        public required List<string> RemainingDirectories { get; init; }
    }
}
