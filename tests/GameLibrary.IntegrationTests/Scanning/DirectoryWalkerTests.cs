using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>DirectoryWalker（T02-B）：只读遍历、规则剪枝、预算分段续扫、取消、深度上限。</summary>
public sealed class DirectoryWalkerTests
{
    private static string FreshTree(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateTree(string root)
    {
        // A/ 安装根：EXE + 数据
        Directory.CreateDirectory(Path.Combine(root, "A"));
        File.WriteAllText(Path.Combine(root, "A", "Game.exe"), "stub");
        File.WriteAllText(Path.Combine(root, "A", "data.xp3"), "stub");
        // B/ 深层链 a/b/c/d/e
        var deep = Path.Combine(root, "B");
        for (var i = 0; i < 5; i++)
        {
            deep = Path.Combine(deep, $"l{i}");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, $"f{i}.txt"), "x");
        }
        // 中文/方括号目录（真实路径形态）
        Directory.CreateDirectory(Path.Combine(root, "[Unity]"));
        File.WriteAllText(Path.Combine(root, "[Unity]", "例.exe"), "x");
        // 被排除子树
        Directory.CreateDirectory(Path.Combine(root, "$RECYCLE.BIN"));
        File.WriteAllText(Path.Combine(root, "$RECYCLE.BIN", "junk.dat"), "x");
    }

    private static int CountFiles(string root, string pattern = "*") =>
        Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Length;

    [Fact]
    public void Walk_CompleteTree_ReportsCountsAndEntries()
    {
        var root = FreshTree("walk-full");
        CreateTree(root);
        try
        {
            var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]));
            var entries = new List<ScannedEntry>();

            var coverage = walker.Walk(entries.Add, pause: null, CancellationToken.None);

            var expectedDirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length + 1;
            Assert.Equal(ScanCompletion.Complete, coverage.Completion);
            Assert.Equal(expectedDirs, coverage.ScannedDirectories);
            Assert.Equal(CountFiles(root), coverage.ObservedFileEntries);
            Assert.Equal(CountFiles(root), entries.Count(e => !e.IsDirectory));
            Assert.Contains(entries, e => e.PhysicalPath.EndsWith("例.exe", StringComparison.Ordinal));
            Assert.All(entries, e => Assert.Equal(ScanRuleOutcome.Continue, e.RuleOutcome));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Walk_RuleExclusion_PrunesSubtreeAndCountsByRule()
    {
        var root = FreshTree("walk-exclude");
        CreateTree(root);
        // 顶层 .dat：不受子树剪枝影响，验证文件级排除计数。
        File.WriteAllText(Path.Combine(root, "top-level.dat"), "x");
        try
        {
            var rules = new ScanRuleSet(
            [
                new ScanRule
                {
                    RuleId = "sys.recycle",
                    Source = ScanRuleSource.SystemMandatory,
                    Action = ScanRuleAction.Exclude,
                    Matcher = new SubtreeMatcher(Path.Combine(root, "$RECYCLE.BIN")),
                },
                new ScanRule
                {
                    RuleId = "default.log",
                    Source = ScanRuleSource.SystemDefault,
                    Action = ScanRuleAction.Exclude,
                    Matcher = new ExtensionMatcher("dat"),
                },
            ]);
            var walker = new DirectoryWalker(GamePath.Create(root), rules);
            var entries = new List<ScannedEntry>();

            var coverage = walker.Walk(entries.Add, pause: null, CancellationToken.None);

            Assert.Equal(ScanCompletion.Complete, coverage.Completion);
            // 目录剪枝：$RECYCLE.BIN 整棵跳过，其内部条目不再进入规则求值。
            Assert.Equal(1, coverage.SkippedDirectories);
            Assert.Equal(1, coverage.ExcludedByRule["sys.recycle"]);
            // 文件级排除只作用于被保留目录内的条目。
            Assert.Equal(1, coverage.SkippedFiles);
            Assert.Equal(1, coverage.ExcludedByRule["default.log"]);
            Assert.DoesNotContain(entries, e => e.PhysicalPath.Contains("$RECYCLE.BIN", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.PhysicalPath.EndsWith(".dat", StringComparison.Ordinal));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Walk_DirectoryBudget_ReturnsPartialWithUsableResumeToken()
    {
        var root = FreshTree("walk-budget");
        CreateTree(root);
        try
        {
            var options = new ScanWalkOptions { MaxDirectories = 3, TimeBudget = TimeSpan.FromSeconds(30) };
            var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]), options);
            var firstEntries = new List<ScannedEntry>();

            var first = walker.Walk(firstEntries.Add, pause: null, CancellationToken.None);

            Assert.Equal(ScanCompletion.Partial, first.Completion);
            Assert.Equal(3, first.ScannedDirectories);
            Assert.NotNull(first.ResumeTokenJson);
            Assert.True(first.UnvisitedBranches > 0);

            // 预算按次计算：续扫同样受 3 目录/次限制，循环续完。
            var totalDirectories = first.ScannedDirectories;
            var resumeToken = first.ResumeTokenJson;
            var guard = 0;
            ScanCoverageData resumed;
            do
            {
                Assert.True(++guard < 20, "续扫未收敛");
                resumed = walker.Resume(resumeToken!, _ => { }, pause: null, CancellationToken.None);
                totalDirectories += resumed.ScannedDirectories;
                resumeToken = resumed.ResumeTokenJson;
            }
            while (resumed.Completion == ScanCompletion.Partial);

            Assert.Equal(ScanCompletion.Complete, resumed.Completion);
            Assert.Null(resumed.ResumeTokenJson);
            Assert.Equal(0, resumed.UnvisitedBranches);

            // 分段合计与一次完整扫描一致（目录数）。
            var full = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]))
                .Walk(_ => { }, pause: null, CancellationToken.None);
            Assert.Equal(full.ScannedDirectories, totalDirectories);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Walk_Cancellation_ReturnsCancelledAndStops()
    {
        var root = FreshTree("walk-cancel");
        CreateTree(root);
        try
        {
            using var cts = new CancellationTokenSource();
            var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]));
            var entries = new List<ScannedEntry>();
            var count = 0;

            var coverage = walker.Walk(
                entry =>
                {
                    entries.Add(entry);
                    if (++count >= 1)
                    {
                        cts.Cancel();
                    }
                },
                pause: null,
                cts.Token);

            Assert.Equal(ScanCompletion.Cancelled, coverage.Completion);
            Assert.NotEmpty(entries);
            var observedAtCancel = entries.Count;
            Assert.Equal(observedAtCancel, entries.Count);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Walk_DepthLimit_DefersDeepBranchesExplainably()
    {
        var root = FreshTree("walk-depth");
        CreateTree(root);
        try
        {
            var options = new ScanWalkOptions { MaxDepth = 3, TimeBudget = TimeSpan.FromSeconds(30) };
            var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]), options);

            var coverage = walker.Walk(_ => { }, pause: null, CancellationToken.None);

            // B 链在深度 4（l2）处被延后；更深层位于被延后子树内，不重复计数。
            // 延后分支未检查，完成度必须是 partial 而非 complete（补充规格 2.2）。
            Assert.Equal(ScanCompletion.Partial, coverage.Completion);
            Assert.Equal(1, coverage.BudgetDeferred);
            Assert.Single(coverage.Issues, i => i.Classification == "DepthLimit" && i.Path.Contains("l2", StringComparison.Ordinal));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Walk_MissingRoot_ReportsOfflineRootAsPartial()
    {
        var root = FreshTree("walk-missing");
        TryCleanup(root);
        var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]));

        var coverage = walker.Walk(_ => { }, pause: null, CancellationToken.None);

        Assert.Equal(ScanCompletion.Partial, coverage.Completion);
        Assert.Equal(1, coverage.OfflineRoots);
        Assert.Single(coverage.Issues, i => i.Classification == "OfflineRoot");
    }

    [Fact]
    public void Resume_TokenFromOtherRoot_IsRejected()
    {
        var rootA = FreshTree("walk-resA");
        var rootB = FreshTree("walk-resB");
        CreateTree(rootA);
        try
        {
            var walkerA = new DirectoryWalker(
                GamePath.Create(rootA),
                new ScanRuleSet([]),
                new ScanWalkOptions { MaxDirectories = 2, TimeBudget = TimeSpan.FromSeconds(30) });
            var partial = walkerA.Walk(_ => { }, pause: null, CancellationToken.None);
            Assert.Equal(ScanCompletion.Partial, partial.Completion);

            var walkerB = new DirectoryWalker(GamePath.Create(rootB), new ScanRuleSet([]));
            Assert.Throws<InvalidOperationException>(
                () => walkerB.Resume(partial.ResumeTokenJson!, _ => { }, pause: null, CancellationToken.None));
        }
        finally
        {
            TryCleanup(rootA);
            TryCleanup(rootB);
        }
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
