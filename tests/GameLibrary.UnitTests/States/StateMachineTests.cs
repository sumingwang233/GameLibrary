using GameLibrary.Domain.States;
using Xunit;

namespace GameLibrary.UnitTests.States;

public sealed class StateMachineTests
{
    [Theory]
    [InlineData(CandidateReviewState.Observed, CandidateReviewState.Stabilizing, true)]
    [InlineData(CandidateReviewState.Stabilizing, CandidateReviewState.PendingReview, true)]
    [InlineData(CandidateReviewState.PendingReview, CandidateReviewState.Accepted, true)]
    [InlineData(CandidateReviewState.PendingReview, CandidateReviewState.Deferred, true)]
    [InlineData(CandidateReviewState.PendingReview, CandidateReviewState.Ignored, true)]
    [InlineData(CandidateReviewState.PendingReview, CandidateReviewState.Unavailable, true)]
    [InlineData(CandidateReviewState.Unavailable, CandidateReviewState.PendingReview, true)]
    [InlineData(CandidateReviewState.Deferred, CandidateReviewState.PendingReview, true)]
    [InlineData(CandidateReviewState.Ignored, CandidateReviewState.Observed, true)]
    [InlineData(CandidateReviewState.Accepted, CandidateReviewState.PendingReview, false)]
    [InlineData(CandidateReviewState.Accepted, CandidateReviewState.Observed, false)]
    [InlineData(CandidateReviewState.Ignored, CandidateReviewState.PendingReview, false)]
    [InlineData(CandidateReviewState.Observed, CandidateReviewState.Accepted, false)]
    [InlineData(CandidateReviewState.Unavailable, CandidateReviewState.Accepted, false)]
    public void ReviewTransitions_EnforceDocumentedRules(CandidateReviewState from, CandidateReviewState to, bool allowed)
    {
        Assert.Equal(allowed, StateMachines.CanTransition(from, to));
    }

    [Fact]
    public void Availability_FirstMiss_BecomesSuspectedMissing()
    {
        var now = DateTime.UtcNow;

        var evaluation = GameAvailabilityTracker.RecordFullCheck(
            GameAvailability.Available, present: false, rootOnline: true, missingSinceUtc: null, utcNow: now);

        Assert.Equal(GameAvailability.SuspectedMissing, evaluation.NewState);
        Assert.Equal(now, evaluation.MissingSinceUtc);
    }

    [Fact]
    public void Availability_SecondMissWithin60Seconds_StaysSuspected()
    {
        var first = DateTime.UtcNow;
        var second = first.AddSeconds(30);

        var evaluation = GameAvailabilityTracker.RecordFullCheck(
            GameAvailability.SuspectedMissing, present: false, rootOnline: true, missingSinceUtc: first, utcNow: second);

        Assert.Equal(GameAvailability.SuspectedMissing, evaluation.NewState);
    }

    [Fact]
    public void Availability_SecondMissAfter60Seconds_BecomesMissing()
    {
        var first = DateTime.UtcNow;
        var second = first.AddSeconds(61);

        var evaluation = GameAvailabilityTracker.RecordFullCheck(
            GameAvailability.SuspectedMissing, present: false, rootOnline: true, missingSinceUtc: first, utcNow: second);

        Assert.Equal(GameAvailability.Missing, evaluation.NewState);
        Assert.Equal(first, evaluation.MissingSinceUtc);
    }

    [Fact]
    public void Availability_Offline_DoesNotAccumulateMissingEvidence()
    {
        var now = DateTime.UtcNow;

        var evaluation = GameAvailabilityTracker.RecordFullCheck(
            GameAvailability.SuspectedMissing, present: false, rootOnline: false, missingSinceUtc: now, utcNow: now.AddSeconds(90));

        Assert.Equal(GameAvailability.Offline, evaluation.NewState);
        Assert.Equal(now, evaluation.MissingSinceUtc);

        // 恢复在线后的第一次缺失重新计数（ID-05：只有连续两次成功完整核对才 Missing）。
        var backOnline = GameAvailabilityTracker.RecordFullCheck(
            evaluation.NewState, present: false, rootOnline: true, missingSinceUtc: null, utcNow: now.AddSeconds(120));
        Assert.Equal(GameAvailability.SuspectedMissing, backOnline.NewState);
    }

    [Fact]
    public void Availability_Reppearing_ResetsToAvailable()
    {
        var now = DateTime.UtcNow;

        var evaluation = GameAvailabilityTracker.RecordFullCheck(
            GameAvailability.SuspectedMissing, present: true, rootOnline: true, missingSinceUtc: now, utcNow: now);

        Assert.Equal(GameAvailability.Available, evaluation.NewState);
        Assert.Null(evaluation.MissingSinceUtc);
    }

    [Fact]
    public void Availability_Missing_GameDataReturning_GoesAvailable()
    {
        Assert.True(StateMachines.CanTransition(GameAvailability.Missing, GameAvailability.Available));
        Assert.False(StateMachines.CanTransition(GameAvailability.Missing, GameAvailability.SuspectedMissing));
    }
}
