using FluentAssertions;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Services;

public class YieldCalculatorTests
{
    private readonly YieldCalculator _sut = new();

    // ── AnnualizedYield ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0004, 3.504)]   // 0.04%/hour * 24 * 365
    [InlineData(0.001,  8.76)]
    [InlineData(0,      0)]
    public void AnnualizedYield_CalculatesCorrectly(decimal ratePerHour, decimal expected)
    {
        var result = _sut.AnnualizedYield(ratePerHour);
        result.Should().BeApproximately(expected, 0.0001m);
    }

    [Fact]
    public void AnnualizedYield_NegativeRate_ReturnsNegative()
    {
        var result = _sut.AnnualizedYield(-0.0004m);
        result.Should().BeNegative();
        result.Should().BeApproximately(-3.504m, 0.0001m);
    }

    // ── ProjectedPnl ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 0.0004, 24,  9.6)]   // $1000 * 0.04%/hr * 24h
    [InlineData(500,  0.001,   8,  4.0)]
    [InlineData(1000, 0,      24,  0)]
    public void ProjectedPnl_CalculatesCorrectly(
        decimal sizeUsdc, decimal netRatePerHour, decimal hours, decimal expected)
    {
        var result = _sut.ProjectedPnl(sizeUsdc, netRatePerHour, hours);
        result.Should().BeApproximately(expected, 0.0001m);
    }

    [Fact]
    public void ProjectedPnl_ZeroHours_ReturnsZero()
    {
        var result = _sut.ProjectedPnl(1000m, 0.0004m, 0m);
        result.Should().Be(0m);
    }

    [Fact]
    public void ProjectedPnl_ZeroSize_ReturnsZero()
    {
        var result = _sut.ProjectedPnl(0m, 0.0004m, 24m);
        result.Should().Be(0m);
    }

    // ── BreakEvenHours ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 0.0009, 0.0004, 2.25)]   // 0.09% fees / 0.04%/hr = 2.25 h
    [InlineData(500,  0.001,  0.0005, 2.0)]
    public void BreakEvenHours_CalculatesCorrectly(
        decimal sizeUsdc, decimal feeRateTotal, decimal netRatePerHour, decimal expected)
    {
        var result = _sut.BreakEvenHours(sizeUsdc, feeRateTotal, netRatePerHour);
        result.Should().BeApproximately(expected, 0.0001m);
    }

    [Fact]
    public void BreakEvenHours_ZeroYield_ReturnsMaxValue()
    {
        var result = _sut.BreakEvenHours(1000m, 0.0009m, 0m);
        result.Should().Be(decimal.MaxValue);
    }

    [Fact]
    public void BreakEvenHours_NegativeYield_ReturnsMaxValue()
    {
        var result = _sut.BreakEvenHours(1000m, 0.0009m, -0.0004m);
        result.Should().Be(decimal.MaxValue);
    }

    // ── UnrealizedPnl ────────────────────────────────────────────────────────

    [Fact]
    public void UnrealizedPnl_WithAccumulatedFunding_ReturnsAccumulated()
    {
        var pos = new ArbitragePosition
        {
            SizeUsdc            = 1000m,
            EntrySpreadPerHour  = 0.0004m,
            AccumulatedFunding  = 7.50m,
            OpenedAt            = DateTime.UtcNow.AddHours(-10)
        };

        var result = _sut.UnrealizedPnl(pos);

        result.Should().Be(7.50m);
    }

    [Fact]
    public void UnrealizedPnl_WithNoAccumulated_EstimatesFromHoursOpen()
    {
        // Position opened exactly 6 hours ago, AccumulatedFunding == 0
        // Expected: 1000 * 0.0004 * 6 = 2.4
        var openedAt = DateTime.UtcNow.AddHours(-6);
        var pos = new ArbitragePosition
        {
            SizeUsdc            = 1000m,
            EntrySpreadPerHour  = 0.0004m,
            AccumulatedFunding  = 0m,
            OpenedAt            = openedAt
        };

        var result = _sut.UnrealizedPnl(pos);

        // Allow ±0.05 tolerance because UtcNow advances slightly during test execution
        result.Should().BeApproximately(2.4m, 0.05m);
    }

    [Fact]
    public void UnrealizedPnl_ZeroAccumulated_ZeroRate_ReturnsZero()
    {
        var pos = new ArbitragePosition
        {
            SizeUsdc            = 1000m,
            EntrySpreadPerHour  = 0m,
            AccumulatedFunding  = 0m,
            OpenedAt            = DateTime.UtcNow.AddHours(-5)
        };

        var result = _sut.UnrealizedPnl(pos);

        result.Should().Be(0m);
    }
}
