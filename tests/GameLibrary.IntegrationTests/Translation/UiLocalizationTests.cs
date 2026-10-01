using GameLibrary.Desktop;
using Xunit;

namespace GameLibrary.IntegrationTests.Translation;

[Collection("UI localization")]
public sealed class UiLocalizationTests
{
    [Fact]
    public void Desktop_UsesSharedCatalog_FormatsValuesAndFallsBack()
    {
        try
        {
            foreach (var (language, label) in new[] { ("zh-CN", "设置"), ("zh-TW", "設定"), ("en", "Settings"), ("ja", "設定") })
            {
                L10n.Apply(language);
                Assert.Equal(label, L10n.T("设置"));
                Assert.Equal("玩家自定义名称", L10n.T("玩家自定义名称"));
            }

            L10n.Apply("en");
            Assert.Equal("Text and interface size: 1.25×", L10n.F($"文字与界面大小：{1.25:0.00}×"));
            Assert.Equal("Launch 游戏名", L10n.F($"启动 {"游戏名"}"));
            L10n.Apply("invalid");
            Assert.Equal("zh-CN", L10n.Language);
        }
        finally
        {
            L10n.Apply("zh-CN");
        }
    }
}

[CollectionDefinition("UI localization", DisableParallelization = true)]
public sealed class UiLocalizationCollection;
