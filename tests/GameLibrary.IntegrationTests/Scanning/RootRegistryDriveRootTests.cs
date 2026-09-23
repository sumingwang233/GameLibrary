using GameLibrary.Contracts;
using GameLibrary.Domain.Paths;
using GameLibrary.Host.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>
/// 库根注册回归（用户实测：无法把盘目录添加为游戏文件夹）：
/// 盘根（"X:\"）是合法库根——尾分隔符是路径本体，TrimEnd 会把它削成
/// "X:"（盘符相对路径）而被 GamePath 拒绝。用真实存在的 C:\ 验证盘根注册、
/// 包含判定与去重；不触碰 F 盘（开发边界）。
/// </summary>
public sealed class RootRegistryDriveRootTests
{
    [Fact]
    public void Add_DriveRoot_IsAccepted()
    {
        var registry = new RootRegistry();
        var root = registry.Add("C:\\");

        Assert.Equal(@"C:\", root.Path.PhysicalPath);
        Assert.True(root.Path.IsDriveRoot);
    }

    [Fact]
    public void Add_DriveRoot_IsIdempotent_AcrossSlashVariants()
    {
        var registry = new RootRegistry();
        var first = registry.Add("C:\\");
        var second = registry.Add("C:\\\\");
        var third = registry.Add("c:\\");

        Assert.Equal(first.RootId, second.RootId);
        Assert.Equal(first.RootId, third.RootId);
        Assert.Single(registry.List());
    }

    [Fact]
    public void Contains_DriveRootCoversChildren()
    {
        var registry = new RootRegistry();
        registry.Add("C:\\");

        Assert.True(registry.Contains("C:\\"));
        Assert.True(registry.Contains(@"C:\Games\SomeGame"));
        Assert.False(registry.Contains(@"D:\Games\SomeGame"));
    }

    [Fact]
    public void AddExisting_DriveRoot_RestoresWithoutTrimDamage()
    {
        var registry = new RootRegistry();
        registry.AddExisting("root-fixed", "C:\\", DateTime.UtcNow);

        var root = Assert.Single(registry.List());
        Assert.Equal(@"C:\", root.Path.PhysicalPath);
        Assert.True(registry.Contains(@"C:\Games\X"));
    }

    [Fact]
    public void Add_DriveRootWithoutSlash_IsStillRejectedAsRelative()
    {
        // "C:"（无分隔符）是盘符相对路径，必须拒绝——防止误把当前目录语义引入。
        var registry = new RootRegistry();
        var ex = Assert.Throws<RootRegistryException>(() => registry.Add("C:"));
        Assert.Equal(ErrorCodes.InvalidPath, ex.Code);
    }

    [Fact]
    public void Add_LibraryChildPath_ReusesExistingParentRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "gamelibrary-root-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(parent, "Games", "Rpg");
        Directory.CreateDirectory(child);
        try
        {
            var registry = new RootRegistry();
            var first = registry.Add(parent);
            var second = registry.Add(child);

            Assert.Equal(first.RootId, second.RootId);
            Assert.Single(registry.ListScannable());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
