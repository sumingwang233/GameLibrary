using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Hosting;
using Xunit;

namespace GameLibrary.IntegrationTests;

public sealed class SingleInstanceGuardTests
{
    private static string TestDir(string testId) =>
        @$"D:\Official\GameLibrary\artifacts\test-runs\{testId}\data";

    [Fact]
    public void SameDirectory_SecondGuardIsNotPrimary()
    {
        var testId = Guid.NewGuid().ToString("N");
        var resolved = DataDirectory.Resolve(TestDir(testId));
        using var first = SingleInstanceGuard.TryAcquire(resolved.CanonicalPath!, resolved.ComparisonKey!);
        using var second = SingleInstanceGuard.TryAcquire(resolved.CanonicalPath!, resolved.ComparisonKey!);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void DifferentDirectories_BothGuardsArePrimary()
    {
        var testId1 = Guid.NewGuid().ToString("N");
        var testId2 = Guid.NewGuid().ToString("N");
        var resolved1 = DataDirectory.Resolve(TestDir(testId1));
        var resolved2 = DataDirectory.Resolve(TestDir(testId2));

        using var guard1 = SingleInstanceGuard.TryAcquire(resolved1.CanonicalPath!, resolved1.ComparisonKey!);
        using var guard2 = SingleInstanceGuard.TryAcquire(resolved2.CanonicalPath!, resolved2.ComparisonKey!);

        Assert.True(guard1.IsPrimary);
        Assert.True(guard2.IsPrimary);
    }

    [Fact]
    public void Dispose_ReleasesGuardForNextAcquire()
    {
        var testId = Guid.NewGuid().ToString("N");
        var resolved = DataDirectory.Resolve(TestDir(testId));

        var first = SingleInstanceGuard.TryAcquire(resolved.CanonicalPath!, resolved.ComparisonKey!);
        Assert.True(first.IsPrimary);
        first.Dispose();

        using var second = SingleInstanceGuard.TryAcquire(resolved.CanonicalPath!, resolved.ComparisonKey!);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void PathAlias_CollapsesToSameGuard()
    {
        var testId = Guid.NewGuid().ToString("N");
        var primary = DataDirectory.Resolve(TestDir(testId));
        var alias = DataDirectory.Resolve(@$"D:/official/gameLibrary/artifacts/test-runs/{testId}/data/../data/");
        using var first = SingleInstanceGuard.TryAcquire(primary.CanonicalPath!, primary.ComparisonKey!);
        using var second = SingleInstanceGuard.TryAcquire(alias.CanonicalPath!, alias.ComparisonKey!);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }
}
