using GameLibrary.Domain.Identity;
using Xunit;

namespace GameLibrary.UnitTests.Identity;

public sealed class GameIdTests
{
    [Fact]
    public void NewGameId_GeneratesDistinctRandomGuids()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => GameId.NewGameId()).ToHashSet();

        Assert.Equal(1000, ids.Count);
    }

    [Fact]
    public void Parse_RoundTripsToString()
    {
        var id = GameId.NewGameId();

        var parsed = GameId.Parse(id.ToString());

        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData(null)]
    public void TryParse_RejectsInvalidText(string? text)
    {
        Assert.False(GameId.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_AcceptsGuidText()
    {
        Assert.True(GameId.TryParse("01234567-89ab-cdef-0123-456789abcdef", out var id));
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", id.ToString());
    }
}

public sealed class MatchFingerprintTests
{
    private static MatchFingerprint.FingerprintEntry Entry(string key, string sha, long size) =>
        new() { RelativeKey = key, Sha256 = sha, SizeBytes = size };

    [Fact]
    public void Fingerprints_WithSameEntries_AreEqual()
    {
        var a = new MatchFingerprint { StrategyVersion = 1, Entries = [Entry("game.exe", "abc", 100)] };
        var b = new MatchFingerprint { StrategyVersion = 1, Entries = [Entry("game.exe", "abc", 100)] };

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentStrategyVersions_AreNotComparable()
    {
        var a = new MatchFingerprint { StrategyVersion = 1, Entries = [Entry("x", "a", 1)] };
        var b = new MatchFingerprint { StrategyVersion = 2, Entries = [Entry("x", "a", 1)] };

        Assert.False(a.IsComparableWith(b));
        Assert.Equal(0, a.SimilarityWith(b));
    }

    [Fact]
    public void Similarity_MatchesByHashedEntriesOnly()
    {
        var old = new MatchFingerprint
        {
            StrategyVersion = 1,
            Entries = [Entry("game.exe", "hash1", 10), Entry("data.xp3", "hash2", 20), Entry("readme.txt", "hash3", 1)],
        };
        var moved = new MatchFingerprint
        {
            StrategyVersion = 1,
            Entries =
            [
                Entry("game.exe", "hash1", 10),
                Entry("data.xp3", "hash2", 20),
                new() { RelativeKey = "extra.dll", SizeBytes = 5 },
            ],
        };

        var similarity = old.SimilarityWith(moved);

        Assert.True(similarity > 0.5, $"相似度 {similarity} 应大于 0.5");
        Assert.True(similarity < 1.0, $"相似度 {similarity} 不应为 1.0（存在未摘要与新增条目）");
    }

    [Fact]
    public void EntriesWithoutHashes_DoNotParticipateInSimilarity()
    {
        var a = new MatchFingerprint
        {
            StrategyVersion = 1,
            Entries = [new() { RelativeKey = "game.exe", SizeBytes = 10, MissingReason = "unreadable" }],
        };
        var b = new MatchFingerprint { StrategyVersion = 1, Entries = [] };

        Assert.Equal(0, a.SimilarityWith(b));
    }
}
