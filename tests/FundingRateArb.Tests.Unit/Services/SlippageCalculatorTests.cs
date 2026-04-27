using FluentAssertions;
using FundingRateArb.Application.Services;

namespace FundingRateArb.Tests.Unit.Services;

public class SlippageCalculatorTests
{
    [Fact]
    public void Compute_BothInputsKnown_ReturnsCorrectPct()
    {
        SlippageCalculator.Compute(intendedMid: 100m, fillPrice: 100.05m)
            .Should().Be(0.0005m);
    }

    [Fact]
    public void Compute_FillBelowMid_ReturnsNegativePct()
    {
        SlippageCalculator.Compute(100m, 99.95m).Should().Be(-0.0005m);
    }

    [Fact]
    public void Compute_NullIntendedMid_ReturnsNull()
    {
        SlippageCalculator.Compute(null, 100.05m).Should().BeNull();
    }

    [Fact]
    public void Compute_NullFillPrice_ReturnsNull()
    {
        SlippageCalculator.Compute(100m, null).Should().BeNull();
    }

    [Fact]
    public void Compute_ZeroFillPrice_ReturnsNull()
    {
        // Stub-leg case in PositionCloser: Task.FromResult(new OrderResultDto { FilledPrice = 0 })
        SlippageCalculator.Compute(100m, 0m).Should().BeNull();
    }

    [Fact]
    public void Compute_ZeroIntendedMid_ReturnsNull()
    {
        SlippageCalculator.Compute(0m, 100m).Should().BeNull();
    }

    [Fact]
    public void Compute_NegativeIntendedMid_ReturnsNull()
    {
        SlippageCalculator.Compute(-100m, 100m).Should().BeNull();
    }

    [Fact]
    public void ExceedsThreshold_PositiveSlipAboveThreshold_True()
    {
        SlippageCalculator.ExceedsThreshold(0.002m, 0.001m).Should().BeTrue();
    }

    [Fact]
    public void ExceedsThreshold_NegativeSlipAboveThresholdMagnitude_True()
    {
        // Magnitude semantics — sign should not matter
        SlippageCalculator.ExceedsThreshold(-0.002m, 0.001m).Should().BeTrue();
    }

    [Fact]
    public void ExceedsThreshold_BelowThreshold_False()
    {
        SlippageCalculator.ExceedsThreshold(0.0005m, 0.001m).Should().BeFalse();
    }

    [Fact]
    public void ExceedsThreshold_NullSlip_False()
    {
        SlippageCalculator.ExceedsThreshold(null, 0.001m).Should().BeFalse();
    }

    [Fact]
    public void Compute_HighPrecisionInput_ReturnsValueWithinDecimal18_8Capacity()
    {
        // Repository persists slippage pct as decimal(18,8). EF Core truncates server-side,
        // but the helper's output should not exceed magnitudes that would round to ±99999999.99999999.
        // This is a regression sentinel — we don't simulate EF's truncation here.
        var result = SlippageCalculator.Compute(intendedMid: 100m, fillPrice: 100.000000123m);
        result.Should().NotBeNull();
        Math.Abs(result!.Value).Should().BeLessThan(99999999m);
    }
}
