using GameLibrary.Domain.Tools;
using Xunit;

namespace GameLibrary.UnitTests.Tools;

/// <summary>验证状态机（T08）：双结论分开；翻译生效必须先有游戏启动证据。</summary>
public sealed class ToolVerificationRulesTests
{
    [Fact]
    public void GuidedUse_WithoutEvidence_StaysOrBecomesGuided()
    {
        Assert.Equal(ToolVerificationStatus.Guided,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.Unknown, false, false));
        Assert.Equal(ToolVerificationStatus.Guided,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.Guided, false, false));
    }

    [Fact]
    public void GameStarted_Evidence_PromotesToSemiAutomatic()
    {
        Assert.Equal(ToolVerificationStatus.SemiAutomatic,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.Unknown, true, false));
        Assert.Equal(ToolVerificationStatus.SemiAutomatic,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.Guided, true, false));
    }

    [Fact]
    public void TranslationConfirmed_WithoutGameStarted_DoesNotVerify()
    {
        // 仅打开工具窗口/孤立翻译证据不能算一键成功。
        Assert.Equal(ToolVerificationStatus.Unknown,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.Unknown, false, true));
    }

    [Fact]
    public void BothCondictions_PromotesToVerifiedAutomatic()
    {
        Assert.Equal(ToolVerificationStatus.VerifiedAutomatic,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.SemiAutomatic, true, true));
    }

    [Fact]
    public void VerifiedAutomatic_IsTerminalForPositiveEvidence()
    {
        Assert.Equal(ToolVerificationStatus.VerifiedAutomatic,
            ToolVerificationRules.ApplyObservation(ToolVerificationStatus.VerifiedAutomatic, true, false));
    }
}
