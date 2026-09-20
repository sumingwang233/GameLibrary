using System.Security.Cryptography;
using GameLibrary.Domain.Identity;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>
/// 指纹计算器（真实文件 I/O）：>1 MiB 入口首尾 64 KiB、≤1 MiB 全量、
/// 缺失 → MissingReason、整根不可读 → 返回 null 不抛。
/// </summary>
public sealed class MatchFingerprintCalculatorTests
{
    private static string TempDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
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

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    [Fact]
    public void LargeEntry_UsesHeadAndTail64KiB()
    {
        var root = TempDir("fp-headtail");
        try
        {
            // >1 MiB 的入口（64 KiB + 1 MiB + 64 KiB = 1152 KiB）：头部 A、中间 B、尾部 C——期望哈希只由头尾决定。
            var head = Enumerable.Repeat((byte)'A', 64 * 1024).ToArray();
            var middle = Enumerable.Repeat((byte)'B', 1024 * 1024).ToArray();
            var tail = Enumerable.Repeat((byte)'C', 64 * 1024).ToArray();
            var entryPath = Path.Combine(root, "Game.exe");
            using (var stream = new FileStream(entryPath, FileMode.Create))
            {
                stream.Write(head);
                stream.Write(middle);
                stream.Write(tail);
            }

            var fingerprint = MatchFingerprintCalculator.Calculate(root, entryPath, "kirikiri");

            Assert.NotNull(fingerprint);
            Assert.Equal(FingerprintPolicy.StrategyVersion, fingerprint!.StrategyVersion);
            var entry = Assert.Single(fingerprint.Entries);
            Assert.Equal("Game.exe", entry.RelativeKey);
            Assert.Equal(head.Length + middle.Length + tail.Length, entry.SizeBytes);
            var expected = Sha256Hex([.. head, .. tail]);
            Assert.Equal(expected, entry.Sha256);
            Assert.Null(entry.MissingReason);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SmallEntryAndContent_AreFullyHashed()
    {
        var root = TempDir("fp-full");
        try
        {
            var entryBytes = "entry-bytes"u8.ToArray();
            var xp3Bytes = "xp3-bytes"u8.ToArray();
            var readmeBytes = "readme-bytes"u8.ToArray();
            var configBytes = "config-bytes"u8.ToArray();
            var entryPath = Path.Combine(root, "Game.exe");
            File.WriteAllBytes(entryPath, entryBytes);
            File.WriteAllBytes(Path.Combine(root, "data.xp3"), xp3Bytes);
            File.WriteAllBytes(Path.Combine(root, "readme.txt"), readmeBytes);
            File.WriteAllBytes(Path.Combine(root, "config.ini"), configBytes);

            var fingerprint = MatchFingerprintCalculator.Calculate(root, entryPath, "kirikiri");

            Assert.NotNull(fingerprint);
            // 入口 + xp3 + 2 个最小小文件 = 4 条，全部有哈希。
            Assert.Equal(4, fingerprint!.Entries.Count);
            Assert.Equal(
                ["Game.exe", "data.xp3", "config.ini", "readme.txt"],
                fingerprint.Entries.Select(e => e.RelativeKey).ToArray());
            Assert.Equal(Sha256Hex(entryBytes), fingerprint.Entries[0].Sha256);
            Assert.Equal(Sha256Hex(xp3Bytes), fingerprint.Entries[1].Sha256);
            Assert.Equal(Sha256Hex(configBytes), fingerprint.Entries[2].Sha256);
            Assert.Equal(Sha256Hex(readmeBytes), fingerprint.Entries[3].Sha256);
            Assert.All(fingerprint.Entries, entry => Assert.Null(entry.MissingReason));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingEntry_GetsMissingReason_WithoutSha()
    {
        var root = TempDir("fp-missing");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "data.xp3"), "x"u8.ToArray());
            File.WriteAllBytes(Path.Combine(root, "readme.txt"), "y"u8.ToArray());

            // 入口不存在（accept 后文件被移走的竞态形态）。
            var fingerprint = MatchFingerprintCalculator.Calculate(
                root, Path.Combine(root, "Game.exe"), "kirikiri");

            Assert.NotNull(fingerprint);
            var entry = fingerprint!.Entries[0];
            Assert.Equal("Game.exe", entry.RelativeKey);
            Assert.Null(entry.Sha256);
            Assert.Equal("missing", entry.MissingReason);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LargeXp3_ContentEntryIsSizeOnly()
    {
        var root = TempDir("fp-xp3-sizeonly");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Game.exe"), "entry"u8.ToArray());
            File.WriteAllBytes(Path.Combine(root, "data.xp3"), new byte[2 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(root, "readme.txt"), "r"u8.ToArray());
            File.WriteAllBytes(Path.Combine(root, "config.ini"), "c"u8.ToArray());

            var fingerprint = MatchFingerprintCalculator.Calculate(
                root, Path.Combine(root, "Game.exe"), "kirikiri");

            Assert.NotNull(fingerprint);
            var xp3 = fingerprint!.Entries.Single(e => e.RelativeKey == "data.xp3");
            Assert.Null(xp3.Sha256);
            Assert.Equal(2 * 1024 * 1024, xp3.SizeBytes);
            Assert.Null(xp3.MissingReason);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingRoot_ReturnsNull_DoesNotThrow()
    {
        var absent = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"fp-absent-{Guid.NewGuid():N}");

        var fingerprint = MatchFingerprintCalculator.Calculate(absent, null, null);

        Assert.Null(fingerprint);
    }

    [Fact]
    public void SingleFileGame_RootIsFile_ProducesSingleEntry()
    {
        var root = TempDir("fp-file");
        try
        {
            var swf = Path.Combine(root, "game.swf");
            var bytes = "FWS-flash-content"u8.ToArray();
            File.WriteAllBytes(swf, bytes);

            var fingerprint = MatchFingerprintCalculator.Calculate(swf, swf, "flash");

            Assert.NotNull(fingerprint);
            var entry = Assert.Single(fingerprint!.Entries);
            Assert.Equal("game.swf", entry.RelativeKey);
            Assert.Equal(Sha256Hex(bytes), entry.Sha256);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void EntryOutsideRoot_FallsBackToFileNameKey()
    {
        var root = TempDir("fp-outside");
        try
        {
            var entryPath = Path.Combine(root, "Game.exe");
            File.WriteAllBytes(entryPath, "z"u8.ToArray());
            File.WriteAllBytes(Path.Combine(root, "a.bin"), "1"u8.ToArray());

            // 入口在别的盘外路径下（relink 前的旧绝对入口形态）：按文件名在根内寻找。
            var fingerprint = MatchFingerprintCalculator.Calculate(
                root, @"D:\elsewhere\Game.exe", null);

            Assert.NotNull(fingerprint);
            Assert.Equal("Game.exe", fingerprint!.Entries[0].RelativeKey);
            Assert.NotNull(fingerprint.Entries[0].Sha256);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void KirikiriSmokeTree_TwoCopiesAreSimilar()
    {
        // CandidateReviewTests.CreateGameTree 的充实形态：入口 + xp3 + 2 个小内容文件。
        var first = TempDir("fp-copy-a");
        var second = TempDir("fp-copy-b");
        try
        {
            foreach (var root in new[] { first, second })
            {
                File.WriteAllBytes(Path.Combine(root, "Game.exe"), "same-entry"u8.ToArray());
                File.WriteAllBytes(Path.Combine(root, "data.xp3"), "same-xp3"u8.ToArray());
                File.WriteAllBytes(Path.Combine(root, "readme.txt"), "same-readme"u8.ToArray());
                File.WriteAllBytes(Path.Combine(root, "config.ini"), "same-config"u8.ToArray());
            }

            var a = MatchFingerprintCalculator.Calculate(first, Path.Combine(first, "Game.exe"), "kirikiri");
            var b = MatchFingerprintCalculator.Calculate(second, Path.Combine(second, "Game.exe"), "kirikiri");

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(1.0, a!.SimilarityWith(b!));
        }
        finally
        {
            Cleanup(first);
            Cleanup(second);
        }
    }
}
