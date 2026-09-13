using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using Xunit;

namespace GameLibrary.UnitTests.Scan;

public sealed class ScanRuleSetTests
{
    private static GamePath Path(string p) => GamePath.Create(p);

    [Fact]
    public void EmptyRuleSet_ContinuesByDefault()
    {
        var set = new ScanRuleSet([]);

        var decision = set.Decide(Path(@"F:\[Unity]\ExampleC"));

        Assert.Equal(ScanRuleOutcome.Continue, decision.Outcome);
        Assert.Null(decision.WinningRuleId);
    }

    [Fact]
    public void SystemMandatoryExclude_CannotBeBypassedByUserInclude()
    {
        var set = new ScanRuleSet(
        [
            new ScanRule
            {
                RuleId = "sys.recycle",
                Source = ScanRuleSource.SystemMandatory,
                Action = ScanRuleAction.Exclude,
                Matcher = new SubtreeMatcher(@"F:\$RECYCLE.BIN"),
            },
            new ScanRule
            {
                RuleId = "user.include-all",
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.Include,
                Priority = 1000,
                Matcher = new GlobMatcher("**"),
            },
        ]);

        var decision = set.Decide(Path(@"F:\$RECYCLE.BIN\x\y"));

        Assert.Equal(ScanRuleOutcome.Exclude, decision.Outcome);
        Assert.Equal("sys.recycle", decision.WinningRuleId);
        // 强制排除走快速路径，不再求值其余规则；完整命中集由 inspect 单独查询。
        Assert.Equal(["sys.recycle"], decision.MatchedRuleIds);
    }

    [Fact]
    public void UserRules_HigherPriorityWins_ThenOrderIndex()
    {
        var set = new ScanRuleSet(
        [
            new ScanRule
            {
                RuleId = "user.low",
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.Exclude,
                Priority = 10,
                OrderIndex = 0,
                Matcher = new ExtensionMatcher("xp3"),
            },
            new ScanRule
            {
                RuleId = "user.high",
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.HideCandidate,
                Priority = 20,
                OrderIndex = 0,
                Matcher = new ExtensionMatcher("xp3"),
            },
            new ScanRule
            {
                RuleId = "user.same-high-later",
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.Include,
                Priority = 20,
                OrderIndex = 1,
                Matcher = new ExtensionMatcher("xp3"),
            },
        ]);

        var decision = set.Decide(Path(@"F:\Game\data.xp3"));

        Assert.Equal(ScanRuleOutcome.HideCandidate, decision.Outcome);
        Assert.Equal("user.high", decision.WinningRuleId);
    }

    [Fact]
    public void UserInclude_OverridesSystemDefaultExclude()
    {
        var set = new ScanRuleSet(
        [
            new ScanRule
            {
                RuleId = "default.node-modules",
                Source = ScanRuleSource.SystemDefault,
                Action = ScanRuleAction.Exclude,
                Matcher = new BasenameMatcher("node_modules"),
            },
            new ScanRule
            {
                RuleId = "user.keep",
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.Include,
                Priority = 1,
                Matcher = new SubtreeMatcher(@"F:\Keep"),
            },
        ]);

        var underKeep = set.Decide(Path(@"F:\Keep\node_modules"));
        var elsewhere = set.Decide(Path(@"F:\Other\node_modules"));

        Assert.Equal(ScanRuleOutcome.Include, underKeep.Outcome);
        Assert.Equal("user.keep", underKeep.WinningRuleId);
        Assert.Equal(ScanRuleOutcome.Exclude, elsewhere.Outcome);
        Assert.Equal("default.node-modules", elsewhere.WinningRuleId);
    }

    [Fact]
    public void DisabledRules_AreSkipped()
    {
        var set = new ScanRuleSet(
        [
            new ScanRule
            {
                RuleId = "user.off",
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.Exclude,
                Matcher = new GlobMatcher("**"),
                Enabled = false,
            },
        ]);

        Assert.Equal(ScanRuleOutcome.Continue, set.Decide(Path(@"F:\A\B")).Outcome);
    }

    [Fact]
    public void Evaluation_IsIndependentOfRegistrationOrder()
    {
        ScanRule RuleA() => new()
        {
            RuleId = "user.a",
            Source = ScanRuleSource.User,
            Action = ScanRuleAction.Exclude,
            Priority = 5,
            OrderIndex = 0,
            Matcher = new BasenameMatcher("BepInEx"),
        };
        ScanRule RuleB() => new()
        {
            RuleId = "user.b",
            Source = ScanRuleSource.User,
            Action = ScanRuleAction.LowPriority,
            Priority = 9,
            OrderIndex = 0,
            Matcher = new SubtreeMatcher(@"F:\Games"),
        };

        var forward = new ScanRuleSet([RuleA(), RuleB()]);
        var backward = new ScanRuleSet([RuleB(), RuleA()]);

        var target = Path(@"F:\Games\BepInEx\core");
        Assert.Equal(forward.Decide(target), backward.Decide(target));
    }

    [Fact]
    public void SubtreeMatcher_RespectsSegmentBoundaries()
    {
        var set = new ScanRuleSet(
        [
            new ScanRule
            {
                RuleId = "sys.tools",
                Source = ScanRuleSource.SystemMandatory,
                Action = ScanRuleAction.Exclude,
                Matcher = new SubtreeMatcher(@"F:\Game\TOOLS"),
            },
        ]);

        Assert.Equal(ScanRuleOutcome.Exclude, set.Decide(Path(@"F:\Game\TOOLS\MTool\Tool")).Outcome);
        Assert.Equal(ScanRuleOutcome.Exclude, set.Decide(Path(@"F:\Game\TOOLS")).Outcome);
        Assert.Equal(ScanRuleOutcome.Continue, set.Decide(Path(@"F:\Game\TOOLS2\other")).Outcome);
    }

    [Theory]
    [InlineData(@"F:\Games\Game.exe", true)]
    [InlineData(@"F:\Games\game.exe", true)]
    [InlineData(@"F:\Games\Game.txt", false)]
    public void ExtensionMatcher_IsCaseInsensitive(string path, bool expected)
    {
        var matcher = new ExtensionMatcher("exe");

        Assert.Equal(expected, matcher.Matches(GamePath.Create(path)));
    }

    [Theory]
    [InlineData(@"F:\[Unity]", "[Unity]", true)]
    [InlineData(@"F:\[Unity]\ExampleC", "[Unity]/**", true)]
    [InlineData(@"F:\Unity\ExampleC", "[Unity]/**", false)]
    [InlineData(@"F:\[Unity]\Deep\Path", "**/*", true)]
    [InlineData(@"F:\[Unity]", "*/ExampleC", false)]
    [InlineData(@"F:\[Unity]\ExampleC", "*/Example?", true)]
    [InlineData(@"F:\Deep\A\B\C", "Deep/**/C", true)]
    [InlineData(@"F:\Deep\C", "Deep/**/C", true)]
    [InlineData(@"F:\Deep\B\C", "Deep/**/C", true)]
    [InlineData(@"F:\Deep\C\D", "Deep/**/C", false)]
    [InlineData(@"F:\x\Games2", "*/Games", false)]
    public void GlobMatcher_SupportsRestrictedWildcards(string path, string pattern, bool expected)
    {
        var matcher = new GlobMatcher(pattern);

        Assert.Equal(expected, matcher.Matches(GamePath.Create(path)));
    }
}
