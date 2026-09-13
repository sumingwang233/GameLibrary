using System.Runtime.InteropServices.ComTypes;
using GameLibrary.Domain.Paths;
using GameLibrary.Infrastructure.Shell;
using Xunit;

namespace GameLibrary.IntegrationTests.Shell;

/// <summary>LNK 三态解析（T04-B）：夹具用 Windows 原生 ShellLink 接口生成（任务书要求）。</summary>
public sealed class LnkResolverTests
{
    private static string FreshDir()
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"lnk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateShortcut(string linkPath, string target, string arguments = "", string workingDirectory = "")
    {
        ShellLinkInterop.RunOnSta(() =>
        {
            var link = ShellLinkInterop.CreateShellLink();
            link.SetPath(target);
            link.SetArguments(arguments);
            if (workingDirectory.Length > 0)
            {
                link.SetWorkingDirectory(workingDirectory);
            }

            ((IPersistFile)link).Save(linkPath, fRemember: false);
            return true;
        });
    }

    private static LnkResolver ResolverFor(string root) =>
        new([GamePath.Create(root)]);

    [Fact]
    public void ValidShortcut_ResolvesTargetArgumentsAndWorkingDirectory()
    {
        var dir = FreshDir();
        try
        {
            var exe = Path.Combine(dir, "Game.exe");
            File.WriteAllText(exe, "stub");
            var link = Path.Combine(dir, "valid.lnk");
            CreateShortcut(link, exe, "-windowed", dir);

            var resolution = ResolverFor(dir).Resolve(link);

            Assert.True(resolution.IsUsableEntry, resolution.Detail);
            Assert.Equal(LnkStatus.ValidEntry, resolution.Status);
            Assert.Equal(exe, resolution.Info!.TargetPath);
            Assert.Equal("-windowed", resolution.Info.Arguments);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void BrokenShortcut_TargetMissing()
    {
        var dir = FreshDir();
        try
        {
            var gone = Path.Combine(dir, "Deleted.exe");
            File.WriteAllText(gone, "x");
            var link = Path.Combine(dir, "broken.lnk");
            CreateShortcut(link, gone);
            File.Delete(gone);

            var resolution = ResolverFor(dir).Resolve(link);

            Assert.Equal(LnkStatus.MissingTarget, resolution.Status);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void ShortcutChain_IsFlattenedToFinalTargetByShell()
    {
        var dir = FreshDir();
        try
        {
            var exe = Path.Combine(dir, "Game.exe");
            File.WriteAllText(exe, "x");
            var inner = Path.Combine(dir, "inner.lnk");
            var outer = Path.Combine(dir, "outer.lnk");
            // Windows 接口在保存时即把 outer→inner 解析为 outer→exe：
            // 循环快捷方式无法经 Shell 接口产生（指向不存在 .lnk 被拒绝、
            // 指向已存在 .lnk 被展平）。解析器的链式/Cyclic 分支是对第三方
            // 工具写入的原始 .lnk 目标的防御，不以伪结构夹具证明。
            CreateShortcut(inner, exe);
            CreateShortcut(outer, inner);

            var resolution = ResolverFor(dir).Resolve(outer);

            Assert.True(resolution.IsUsableEntry, resolution.Detail);
            Assert.Equal(exe, resolution.Info!.TargetPath);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void DirectoryShortcut_IsNavigationOnly()
    {
        var dir = FreshDir();
        try
        {
            var sub = Path.Combine(dir, "Games");
            Directory.CreateDirectory(sub);
            var link = Path.Combine(dir, "dir.lnk");
            CreateShortcut(link, sub);

            var resolution = ResolverFor(dir).Resolve(link);

            Assert.Equal(LnkStatus.DirectoryTarget, resolution.Status);
            Assert.False(resolution.IsUsableEntry);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void ShellCommandShortcut_IsNotAutoExecuted()
    {
        var dir = FreshDir();
        try
        {
            var link = Path.Combine(dir, "cmd.lnk");
            CreateShortcut(link, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c start game.bat");

            var resolution = ResolverFor(dir).Resolve(link);

            Assert.Equal(LnkStatus.ShellCommand, resolution.Status);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void TargetOutsideAllowedRoots_IsOutOfScope()
    {
        var dir = FreshDir();
        var other = FreshDir();
        try
        {
            var exe = Path.Combine(other, "Game.exe");
            File.WriteAllText(exe, "x");
            var link = Path.Combine(dir, "outside.lnk");
            CreateShortcut(link, exe);

            var resolution = ResolverFor(dir).Resolve(link);

            Assert.Equal(LnkStatus.OutOfScopeTarget, resolution.Status);
        }
        finally
        {
            TryCleanup(dir);
            TryCleanup(other);
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
