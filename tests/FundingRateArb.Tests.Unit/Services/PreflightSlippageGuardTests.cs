using FluentAssertions;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Services;

public class PreflightSlippageGuardTests
{
    // ── Baseline cap ────────────────────────────────────────────────────────

    [Fact]
    public void GetCurrentCap_NoReverts_ReturnsBaseline()
    {
        var guard = new PreflightSlippageGuard();
        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline);
    }

    [Fact]
    public void ShouldReject_SlippageBelowBaseline_ReturnsFalse()
    {
        var guard = new PreflightSlippageGuard();
        guard.ShouldReject("Lighter", "ETH", 0.004m).Should().BeFalse();
    }

    [Fact]
    public void ShouldReject_SlippageAboveBaseline_ReturnsTrue()
    {
        var guard = new PreflightSlippageGuard();
        guard.ShouldReject("Lighter", "ETH", 0.006m).Should().BeTrue();
    }

    // ── Halve at 3 reverts ───────────────────────────────────────────────

    [Fact]
    public void GetCurrentCap_ThreeReverts_ReturnsHalfBaseline()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 3; i++)
        {
            guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.Slippage);
        }

        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline / 2m);
    }

    [Fact]
    public void ShouldReject_AfterThreeReverts_UsesHalvedCap()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 3; i++)
        {
            guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.Slippage);
        }

        // 0.003 > Baseline/2 (0.0025) → reject
        guard.ShouldReject("Lighter", "ETH", 0.003m).Should().BeTrue();
        // 0.002 <= Baseline/2 → pass
        guard.ShouldReject("Lighter", "ETH", 0.002m).Should().BeFalse();
    }

    // ── Quarter at 5 reverts ─────────────────────────────────────────────

    [Fact]
    public void GetCurrentCap_FiveReverts_ReturnsQuarterBaseline()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.InsufficientDepth);
        }

        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline / 4m);
    }

    // ── Decay back to baseline after quiet window ────────────────────────

    [Fact]
    public void GetCurrentCap_AfterQuietWindow_DecaysToBaseline()
    {
        // Use a controllable clock — inject old timestamps
        var past = DateTime.UtcNow - PreflightSlippageGuard.Window - TimeSpan.FromMinutes(1);
        var clock = new Queue<DateTime>(new[] { past, past, past, DateTime.UtcNow });
        var guard = new PreflightSlippageGuard(() => clock.Count > 0 ? clock.Dequeue() : DateTime.UtcNow);

        // Record 3 reverts in the "past" (outside the window)
        guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.Slippage);
        guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.Slippage);
        guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.Slippage);

        // Now the clock is at "now" — old reverts are outside the 30-min window
        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline);
    }

    // ── Non-depth reasons do not feed counter ────────────────────────────

    [Fact]
    public void RecordRevert_MarginInsufficient_DoesNotFeedCounter()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.MarginInsufficient);
        }

        // Cap should still be at baseline — margin reverts don't count
        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline);
    }

    [Fact]
    public void RecordRevert_BalanceInsufficient_DoesNotFeedCounter()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.BalanceInsufficient);
        }

        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline);
    }

    // ── Per-(exchange, asset) isolation ─────────────────────────────────

    [Fact]
    public void RecordRevert_OnAsset_DoesNotAffectOtherAsset()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordRevert("Lighter", "YZY", LighterOrderRevertReason.Slippage);
        }

        guard.GetCurrentCap("Lighter", "ETH").Should().Be(PreflightSlippageGuard.Baseline);
    }

    [Fact]
    public void RecordRevert_OnExchange_DoesNotAffectOtherExchange()
    {
        var guard = new PreflightSlippageGuard();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordRevert("Lighter", "ETH", LighterOrderRevertReason.Slippage);
        }

        guard.GetCurrentCap("Hyperliquid", "ETH").Should().Be(PreflightSlippageGuard.Baseline);
    }

    // ── Absolute-value slippage comparison ───────────────────────────────

    [Fact]
    public void ShouldReject_NegativeSlippage_UsesAbsoluteValue()
    {
        var guard = new PreflightSlippageGuard();
        // -0.006 → abs = 0.006 > baseline 0.005 → reject
        guard.ShouldReject("Lighter", "ETH", -0.006m).Should().BeTrue();
        // -0.004 → abs = 0.004 < baseline → pass
        guard.ShouldReject("Lighter", "ETH", -0.004m).Should().BeFalse();
    }
}
