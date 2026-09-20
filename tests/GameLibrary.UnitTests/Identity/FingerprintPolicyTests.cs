using GameLibrary.Domain.Identity;
using Xunit;

namespace GameLibrary.UnitTests.Identity;

/// <summary>
/// 指纹选型纯逻辑（FingerprintPolicy，StrategyVersion=1）：
/// 各引擎关键文件命中、引擎运行时/壳排除、条目与字节预算、未知引擎回退。
/// </summary>
public sealed class FingerprintPolicyTests
{
    private static FingerprintFile File(string path, long size) => new(path, size);

    private static IReadOnlyList<string> Keys(IReadOnlyList<FingerprintSelection> selections) =>
        selections.Select(s => s.RelativePath).ToArray();

    [Fact]
    public void Unity_SelectsGlobalGameManagers_AndSmallestDataFiles()
    {
        var files = new[]
        {
            File("Game.exe", 5_000_000),
            File("UnityPlayer.dll", 30_000_000),
            File("Game_Data/globalgamemanagers", 2_000_000),
            File("Game_Data/boot.config", 300),
            File("Game_Data/small.asset", 900),
            File("Game_Data/big.asset", 900_000_000),
            File("Game_Data/Managed/lib.dll", 500),
            File("Game_Data/Plugins/native.dll", 600),
            File("Game_Data/StreamingAssets/extra.bin", 400),
        };

        var selections = FingerprintPolicy.Select("unity", "Game.exe", files);

        // 入口在前 + globalgamemanagers（判定标记）+ 2 个最小 ≤1MiB 的 *_Data 文件（含一级子目录）。
        Assert.Equal(
            ["Game.exe", "Game_Data/globalgamemanagers", "Game_Data/boot.config", "Game_Data/StreamingAssets/extra.bin"],
            Keys(selections));
        // 入口 >1MiB → 首尾模式；globalgamemanagers >1MiB → 只记大小；小文件全量。
        Assert.Equal(FingerprintHashMode.HeadTail64KiB, selections[0].Mode);
        Assert.Equal(FingerprintHashMode.SizeOnly, selections[1].Mode);
        Assert.Equal(FingerprintHashMode.Full, selections[2].Mode);
        Assert.Equal(FingerprintHashMode.Full, selections[3].Mode);
        // Managed/、Plugins/、UnityPlayer.dll、大资产被排除。
        Assert.DoesNotContain(Keys(selections), key => key.Contains("Managed") || key.Contains("Plugins"));
        Assert.DoesNotContain("UnityPlayer.dll", Keys(selections));
        Assert.DoesNotContain("Game_Data/big.asset", Keys(selections));
    }

    [Theory]
    [InlineData("www/data/System.json", "package.json", "www/img/faces.png")]
    [InlineData("data/System.json", "www/package.json", "img/faces.png")]
    public void RpgMakerMvMz_SelectsSystemJsonAndPackage_ThenImgFill(
        string systemJson, string packageJson, string imgFile)
    {
        var files = new[]
        {
            File("nw.exe", 60_000_000),
            File("www/js/rpg_core.js", 1_500_000),
            File(systemJson, 4_000),
            File(packageJson, 500),
            File(imgFile, 2_000),
            File("www/img/pictures/large.png", 8_000_000),
        };

        var selections = FingerprintPolicy.Select("rpgMakerMvMz", "nw.exe", files);

        Assert.Equal(["nw.exe", systemJson, packageJson, imgFile], Keys(selections));
        // 不选 core-js（引擎运行时，跨游戏同版字节相同）与大图（>1MiB）。
        Assert.DoesNotContain("www/js/rpg_core.js", Keys(selections));
        Assert.DoesNotContain("www/img/pictures/large.png", Keys(selections));
        Assert.All(selections.Skip(1), selection => Assert.Equal(FingerprintHashMode.Full, selection.Mode));
    }

    [Fact]
    public void RpgMakerMvMz_WithoutImgDir_FillsNothing_ButKeepsDetectorPicks()
    {
        var files = new[]
        {
            File("Game.exe", 1_000),
            File("www/data/System.json", 4_000),
            File("www/package.json", 500),
        };

        var selections = FingerprintPolicy.Select("rpgMakerMvMz", "Game.exe", files);

        Assert.Equal(["Game.exe", "www/data/System.json", "www/package.json"], Keys(selections));
    }

    [Fact]
    public void Renpy_SelectsSmallestThreeUnderGame_ExcludesRenpyRuntime()
    {
        var files = new[]
        {
            File("Game.exe", 70_000_000),
            File("renpy/common.rpyb", 900),
            File("game/script.rpy", 30_000),
            File("game/gui.rpy", 500),
            File("game/options.rpy", 700),
            File("game/images.rpy", 10_000),
            File("game/big.rpa", 40_000_000),
        };

        var selections = FingerprintPolicy.Select("renpy", "Game.exe", files);

        // renpy/ 引擎运行时与大 .rpa（>1MiB）排除；game/ 下最小 3 个 ≤1MiB（500/700/10000）。
        Assert.Equal(["Game.exe", "game/gui.rpy", "game/options.rpy", "game/images.rpy"], Keys(selections));
    }

    [Fact]
    public void Kirikiri_SelectsSmallestXp3_AndSmallNonExeFill()
    {
        var files = new[]
        {
            File("Game.exe", 3_000_000),
            File("data.xp3", 500_000_000),
            File("voice.xp3", 800_000_000),
            File("readme.txt", 100),
            File("config.ini", 50),
            File("save.dat", 400),
            File("launcher.exe", 200),
            File("sub/font.ttf", 300),
        };

        var selections = FingerprintPolicy.Select("kirikiri", "Game.exe", files);

        // 入口 + 最小 xp3（>1MiB 只记大小）+ 根/一级子目录最小 2 个 ≤1MiB 非 exe 文件。
        Assert.Equal(["Game.exe", "data.xp3", "config.ini", "readme.txt"], Keys(selections));
        Assert.Equal(FingerprintHashMode.SizeOnly, selections[1].Mode);
        Assert.DoesNotContain("launcher.exe", Keys(selections));
    }

    [Fact]
    public void Flash_SelectsEntryOnly()
    {
        var files = new[]
        {
            File("game.swf", 9_000_000),
            File("player.exe", 2_000_000),
        };

        var selections = FingerprintPolicy.Select("flash", "game.swf", files);

        // .swf 即入口，单条 hashed；v1 不出内容建议。
        Assert.Equal(["game.swf"], Keys(selections));
        Assert.Equal(FingerprintHashMode.HeadTail64KiB, selections[0].Mode);
    }

    [Fact]
    public void UnknownEngine_FallsBackToEntryPlusSmallestRootFiles()
    {
        var files = new[]
        {
            File("Start.exe", 2_000_000),
            File("b.bin", 200),
            File("a.bin", 100),
            File("c.bin", 300),
            File("d.bin", 400),
            File("UnityPlayer.dll", 150),
            File("inner/x.bin", 50),
        };

        var selections = FingerprintPolicy.Select(null, "Start.exe", files);

        // 入口 + 根目录最小 3 个 ≤1MiB（排除运行时清单；inner/ 不在根）。
        Assert.Equal(["Start.exe", "a.bin", "b.bin", "c.bin"], Keys(selections));
    }

    [Fact]
    public void EntrySmallerThan1MiB_IsFullyHashed()
    {
        var files = new[] { File("Game.exe", 600_000) };

        var selections = FingerprintPolicy.Select("kirikiri", "Game.exe", files);

        Assert.Equal(FingerprintHashMode.Full, selections[0].Mode);
    }

    [Fact]
    public void MissingEntry_ProducesSelectionWithNullSize()
    {
        var files = new[] { File("data.xp3", 100), File("readme.txt", 10) };

        var selections = FingerprintPolicy.Select("kirikiri", "Game.exe", files);

        // 入口不在清单：仍产出条目（SizeBytes=null，执行器记 MissingReason），内容照常选择。
        Assert.Equal("Game.exe", selections[0].RelativePath);
        Assert.Null(selections[0].SizeBytes);
        Assert.Equal("data.xp3", selections[1].RelativePath);
    }

    [Fact]
    public void Budget_TotalEntriesAndContentBytesStayWithinCaps()
    {
        var files = new[]
        {
            File("Game.exe", 900_000),
            File("a.xp3", 1_048_576),
            File("b.xp3", 1_048_575),
            File("c.xp3", 1_048_574),
            File("d.xp3", 1_048_573),
            File("readme.txt", 1_048_576),
            File("config.ini", 1_048_576),
        };

        var selections = FingerprintPolicy.Select("kirikiri", "Game.exe", files);

        // 条目总数 ≤5（入口 1 + 内容 ≤3）。
        Assert.True(selections.Count <= 5, $"条目数 {selections.Count} 超上限");
        // 内容文件全量哈希字节总计 ≤4 MiB。
        var contentBytes = selections.Skip(1)
            .Where(s => s.Mode == FingerprintHashMode.Full)
            .Sum(s => s.SizeBytes!.Value);
        Assert.True(contentBytes <= FingerprintPolicy.ContentHashBudgetBytes,
            $"内容哈希字节 {contentBytes} 超预算");
        // 无重复条目。
        Assert.Equal(selections.Count, Keys(selections).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ExcludedRuntimeNames_AreNeverContentCandidates()
    {
        var files = new[]
        {
            File("Game.exe", 10),
            File("nw.exe", 20),
            File("rpg_core.js", 30),
            File("rmmz_core.js", 40),
            File("UnityPlayer.dll", 50),
            File("real.dat", 60),
        };

        var selections = FingerprintPolicy.Select(null, "Game.exe", files);

        // 运行时/壳清单全部排除（入口豁免：Game.exe 本身就是入口）。
        Assert.DoesNotContain(Keys(selections), key =>
            key is "nw.exe" or "rpg_core.js" or "rmmz_core.js" or "UnityPlayer.dll");
        Assert.Equal("real.dat", selections[1].RelativePath);
    }

    [Fact]
    public void StrategyVersion_IsOne()
    {
        Assert.Equal(1, FingerprintPolicy.StrategyVersion);
    }
}
