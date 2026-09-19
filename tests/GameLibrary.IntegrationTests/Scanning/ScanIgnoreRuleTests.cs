using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>
/// R36 扫描过滤名单接线（补充规格 2.1 / ADR-0002）：ignore 规则映射为遍历层排除，
/// 被忽略分支不再枚举，覆盖报告按规则计数可解释（excludedByRule）。
/// </summary>
public sealed class ScanIgnoreRuleTests
{
    private static string NewFixtureRoot()
    {
        var path = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs", $"scanignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteKirikiriGame(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Game.exe"), "x");
        File.WriteAllText(Path.Combine(path, "data.xp3"), "x");
    }

    private static (CandidateRegistry Registry, ScanCoverageData Coverage) RunScan(
        string rootPath, params IgnoreRule[] ignoreRules)
    {
        var validation = GamePath.TryCreate(rootPath);
        Assert.True(validation.IsValid);
        var registry = new CandidateRegistry();
        var collector = new ScanCandidateCollector(validation.Path!, "job-ignore", registry);
        var context = new JobContext { JobId = "job-ignore", Token = CancellationToken.None };
        ScanCoverageData? completed = null;
        var outcome = ScanJobRunner.Run(
            validation.Path!,
            context,
            collector,
            onCompleted: coverage => completed = coverage,
            rules: ScanIgnoreRuleSet.FromIgnoreRules(ignoreRules));
        Assert.Equal("succeeded", outcome.FinalState);
        Assert.NotNull(completed);
        return (registry, completed!);
    }

    private static IgnoreRule Rule(string scope, string? path, string id = "ignore-1", int revision = 1) => new()
    {
        IgnoreId = id,
        Scope = scope,
        Path = path,
        GameId = scope == "ConfirmedIdentity" ? "game-1" : null,
        Reason = "test",
        Revision = revision,
        CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void SubtreeRule_ExcludesWholeBranchFromWalkAndCandidates()
    {
        var root = NewFixtureRoot();
        try
        {
            WriteKirikiriGame(Path.Combine(root, "GameA"));
            WriteKirikiriGame(Path.Combine(root, "Skipped", "HiddenB"));

            // 尾分隔符输入：经 GamePath 规范化后仍与遍历产生的 ComparisonKey 一致。
            var subtree = Path.Combine(root, "Skipped") + Path.DirectorySeparatorChar;
            var (registry, coverage) = RunScan(root, Rule("Subtree", subtree));

            var candidate = Assert.Single(registry.List());
            Assert.EndsWith("GameA", candidate.PhysicalPath, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(ScanCompletion.Complete, coverage.Completion);
            // root + GameA；Skipped 分支在入栈前被排除，其内部文件从未枚举。
            Assert.Equal(2, coverage.ScannedDirectories);
            Assert.Equal(1, coverage.SkippedDirectories);
            Assert.Equal(2, coverage.ObservedFileEntries);
            Assert.Equal(1, coverage.ExcludedByRule.GetValueOrDefault("ignore-1"));
            Assert.Equal(1, coverage.RuleSetRevision);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void ExactPathRule_ExcludesDirectoryButNotPrefixSibling()
    {
        var root = NewFixtureRoot();
        try
        {
            WriteKirikiriGame(Path.Combine(root, "GameA"));
            WriteKirikiriGame(Path.Combine(root, "GameA2"));

            var (registry, coverage) = RunScan(root, Rule("ExactPath", Path.Combine(root, "GameA")));

            // 分段边界：GameA2 不因前缀相同被连带排除。
            var candidate = Assert.Single(registry.List());
            Assert.EndsWith("GameA2", candidate.PhysicalPath, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(ScanCompletion.Complete, coverage.Completion);
            Assert.Equal(1, coverage.SkippedDirectories);
            Assert.Equal(1, coverage.ExcludedByRule.GetValueOrDefault("ignore-1"));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void ConfirmedIdentityAndInvalidPathRules_DoNotAffectWalk()
    {
        var root = NewFixtureRoot();
        try
        {
            WriteKirikiriGame(Path.Combine(root, "GameA"));
            WriteKirikiriGame(Path.Combine(root, "GameB"));

            // ConfirmedIdentity 按 gameId 绑定候选，遍历层不求值；
            // 相对路径不可求值，防御性跳过而非阻断扫描。
            var (registry, coverage) = RunScan(
                root,
                Rule("ConfirmedIdentity", null),
                Rule("ExactPath", "GameB", id: "ignore-bad"));

            // 两个引擎根均成为候选；根目录本身另计为 Container 候选。
            Assert.Equal(2, registry.List().Count(c => c.Kind == CandidateKind.GameRoot));
            Assert.Equal(ScanCompletion.Complete, coverage.Completion);
            Assert.Equal(3, coverage.ScannedDirectories);
            Assert.Equal(0, coverage.SkippedDirectories);
            Assert.Empty(coverage.ExcludedByRule);
            Assert.Equal(0, coverage.RuleSetRevision);
        }
        finally
        {
            TryCleanup(root);
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
