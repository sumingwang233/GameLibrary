using GameLibrary.Domain.Classification;
using Xunit;

namespace GameLibrary.UnitTests.Classification;

public sealed class ClassificationRulesTests
{
    private readonly ClassificationRules _rules = new();

    [Fact]
    public void AniSegment_IsPublisherTag()
    {
        var result = _rules.Classify(["[ANIM]"]);

        var tag = Assert.Single(result.Tags);
        Assert.Equal(FolderTagKind.Publisher, tag.Kind);
        Assert.Equal("ANIM", tag.CanonicalValue);
        Assert.Equal("[ANIM]", tag.SourceSegment);
    }

    [Fact]
    public void NestedAncestors_KeepEachSource()
    {
        // FS-02：[26.7.27]/[Clockup]/游戏 → 批次 + 厂商，各带来源。
        var result = _rules.Classify(["[26.7.27]", "[Clockup]"]);

        Assert.Equal(2, result.Tags.Count);
        Assert.Contains(result.Tags, t => t.Kind == FolderTagKind.Batch && t.CanonicalValue == "26.7.27");
        Assert.Contains(result.Tags, t => t.Kind == FolderTagKind.Publisher && t.CanonicalValue == "Clockup");
    }

    [Fact]
    public void ToolNeed_InheritsThroughIntermediateLayers()
    {
        // FS-03：[toolNeed]/[Unity]/游戏 → Required 不因中间多一层而丢失。
        var result = _rules.Classify(["[toolNeed]", "[Unity]"]);

        Assert.True(result.RequiredByToolNeed);
        Assert.Equal("[toolNeed]", result.ToolNeedSourceSegment);
        Assert.Contains(result.Tags, t => t.Kind == FolderTagKind.Engine && t.CanonicalValue == "Unity");

        var policy = TranslationPolicy.FromInheritance(result);
        Assert.Equal(TranslationRequirement.Required, policy.Effective);
        Assert.Equal("[toolNeed]", policy.InheritedFrom);
    }

    [Fact]
    public void RpgSegment_IsCategoryNotEngine()
    {
        var result = _rules.Classify(["[RPG]"]);

        var tag = Assert.Single(result.Tags);
        Assert.Equal(FolderTagKind.Category, tag.Kind);
        Assert.NotEqual(FolderTagKind.Engine, tag.Kind);
    }

    [Fact]
    public void KnownCollectionsAndUnknowns_AreDistinguished()
    {
        var known = _rules.Classify(["[MTool_Android]"]);
        var unknown = _rules.Classify(["[随便分类]"]);

        Assert.Equal(FolderTagKind.Collection, Assert.Single(known.Tags).Kind);
        Assert.Equal(FolderTagKind.Category, Assert.Single(unknown.Tags).Kind);
        Assert.Equal("随便分类", unknown.Tags[0].CanonicalValue);
    }

    [Fact]
    public void NonBracketSegments_AreNotContainers()
    {
        var result = _rules.Classify(["[LunaSoft] 某游戏 V1.0", "普通目录"]);

        Assert.Empty(result.Tags);
        Assert.False(result.RequiredByToolNeed);
    }

    [Fact]
    public void TranslationPolicy_UserOverrideBeatsInheritance()
    {
        var inherited = TranslationPolicy.FromInheritance(_rules.Classify(["[toolNeed]"]));
        Assert.True(inherited.IsRequired);

        var overridden = inherited with { UserOverride = TranslationRequirement.NotRequired };
        Assert.False(overridden.IsRequired);
        Assert.Equal(TranslationRequirement.Required, overridden.Inherited);
    }

    [Fact]
    public void MultipleSameKindTags_KeepEverySource()
    {
        var result = _rules.Classify(["[分类A]", "[分类B]"]);

        Assert.Equal(2, result.Tags.Count(t => t.Kind == FolderTagKind.Category));
    }
}
