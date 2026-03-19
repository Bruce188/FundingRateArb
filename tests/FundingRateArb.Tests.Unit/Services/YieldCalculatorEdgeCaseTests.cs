using FluentAssertions;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Services;

public class YieldCalculatorEdgeCaseTests
{
    private readonly YieldCalculator _sut = new();

    // ── Test 9: AccumulatedFunding non-zero → returned directly ───────────────

    [Fact]
    public void UnrealizedPnl_WithNonZeroAccumulatedFunding_ReturnsAccumulated()
    {
        var position = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0.0004m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            AccumulatedFunding = -5.5m,
        };

        var result = _sut.UnrealizedPnl(position);

        result.Should().Be(-5.5m);
    }

    // ── Test 10: AccumulatedFunding=0 → falls back to linear estimate ─────────

    [Fact]
    public void UnrealizedPnl_WithZeroAccumulatedFunding_UsesLinearEstimate()
    {
        // Expected: 1000 * 0.0004 * ~2 hours ≈ 0.8
        var position = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0.0004m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            AccumulatedFunding = 0m,
        };

        var result = _sut.UnrealizedPnl(position);

        // Allow ±0.01 tolerance for execution timing
        result.Should().BeApproximately(0.8m, 0.01m);
    }

    // ── Test 11: Zero net rate → MaxValue ─────────────────────────────────────

    [Fact]
    public void BreakEvenHours_WithZeroNetRate_ReturnsMaxValue()
    {
        var result = _sut.BreakEvenHours(0.001m, 0m);

        result.Should().Be(decimal.MaxValue);
    }

    // ── Test 12: Negative net rate → MaxValue ─────────────────────────────────

    [Fact]
    public void BreakEvenHours_WithNegativeNetRate_ReturnsMaxValue()
    {
        var result = _sut.BreakEvenHours(0.001m, -0.0001m);

        result.Should().Be(decimal.MaxValue);
    }

    // ── Test 13: Negative net rate produces a loss ────────────────────────────

    [Fact]
    public void ProjectedPnl_WithNegativeRate_ReturnsLoss()
    {
        // 1000 * -0.0001 * 24 = -2.4
        var result = _sut.ProjectedPnl(1000m, -0.0001m, 24m);

        result.Should().Be(-2.4m);
    }
}
