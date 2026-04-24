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
    [InlineData(0.001, 8.76)]
    [InlineData(0, 0)]
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
    [InlineData(1000, 0.0004, 24, 9.6)]   // $1000 * 0.04%/hr * 24h
    [InlineData(500, 0.001, 8, 4.0)]
    [InlineData(1000, 0, 24, 0)]
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
    [InlineData(0.0009, 0.0004, 2.25)]   // 0.09% fees / 0.04%/hr = 2.25 h
    [InlineData(0.001, 0.0005, 2.0)]
    public void BreakEvenHours_CalculatesCorrectly(
        decimal feeRateTotal, decimal netRatePerHour, decimal expected)
    {
        var result = _sut.BreakEvenHours(feeRateTotal, netRatePerHour);
        result.Should().BeApproximately(expected, 0.0001m);
    }

    [Fact]
    public void BreakEvenHours_ZeroYield_ReturnsMaxValue()
    {
        var result = _sut.BreakEvenHours(0.0009m, 0m);
        result.Should().Be(decimal.MaxValue);
    }

    [Fact]
    public void BreakEvenHours_NegativeYield_ReturnsMaxValue()
    {
        var result = _sut.BreakEvenHours(0.0009m, -0.0004m);
        result.Should().Be(decimal.MaxValue);
    }

    // ── UnrealizedPnl ────────────────────────────────────────────────────────

    [Fact]
    public void UnrealizedPnl_WithAccumulatedFunding_ReturnsAccumulated()
    {
        var pos = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0.0004m,
            AccumulatedFunding = 7.50m,
            OpenedAt = DateTime.UtcNow.AddHours(-10)
        };

        var result = _sut.UnrealizedPnl(pos);

        result.Should().Be(7.50m);
    }

    [Fact]
    public void UnrealizedPnl_WithNoAccumulated_EstimatesFromHoursOpen()
    {
        // Position opened exactly 6 hours ago, AccumulatedFunding == 0
        // Fallback uses notional: SizeUsdc * Leverage * EntrySpreadPerHour * hoursOpen
        // Expected: 1000 * 5 * 0.0004 * 6 = 12.0
        var openedAt = DateTime.UtcNow.AddHours(-6);
        var pos = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            Leverage = 5,
            EntrySpreadPerHour = 0.0004m,
            AccumulatedFunding = 0m,
            OpenedAt = openedAt
        };

        var result = _sut.UnrealizedPnl(pos);

        // Allow ±0.25 tolerance because UtcNow advances slightly during test execution
        result.Should().BeApproximately(12.0m, 0.25m);
    }

    [Fact]
    public void UnrealizedPnl_ZeroAccumulated_ZeroRate_ReturnsZero()
    {
        var pos = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0m,
            AccumulatedFunding = 0m,
            OpenedAt = DateTime.UtcNow.AddHours(-5)
        };

        var result = _sut.UnrealizedPnl(pos);

        result.Should().Be(0m);
    }

    // ── D8: Negative AccumulatedFunding ─────────────────────────────────────────

    [Fact]
    public void UnrealizedPnl_NegativeAccumulatedFunding_ReturnsNegative()
    {
        var pos = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0.0004m,
            AccumulatedFunding = -5.0m,
            OpenedAt = DateTime.UtcNow.AddHours(-10),
        };

        var result = _sut.UnrealizedPnl(pos);

        result.Should().Be(-5.0m);
    }

    // ── D8: Fallback estimate uses notional ─────────────────────────────────────

    [Fact]
    public void UnrealizedPnl_FallbackEstimate_UsesNotional()
    {
        // AccumulatedFunding=0, SizeUsdc=100, Leverage=5, EntrySpreadPerHour=0.001
        // notional = 100 * 5 = 500
        // Expected: 500 * 0.001 * hours (not 100 * 0.001 * hours)
        var openedAt = DateTime.UtcNow.AddHours(-2);
        var pos = new ArbitragePosition
        {
            SizeUsdc = 100m,
            Leverage = 5,
            EntrySpreadPerHour = 0.001m,
            AccumulatedFunding = 0m,
            OpenedAt = openedAt,
        };

        var result = _sut.UnrealizedPnl(pos);

        // notional * spread * hours = 500 * 0.001 * 2 = 1.0
        result.Should().BeApproximately(1.0m, 0.05m);
    }

    // ── Regression: RawRate-based accrual (Appendix B) ───────────────────────
    // Binance:     8h native interval, RawRate=0.0008 → 24h / 8h = 3 cycles  → 0.0008 × 3  = 0.0024
    // Hyperliquid: 1h native interval, RawRate=0.0001 → 24h / 1h = 24 cycles → 0.0001 × 24 = 0.0024
    // Both legs must accrue equal amounts to 4 decimal places.
    [Fact]
    public void AccruedFunding_BinanceVsHyperliquid_EqualOver24hWindow()
    {
        // Binance: 8-hour native interval, RawRate = 0.0008 per settlement
        decimal binanceAccrual = _sut.AccruedFunding(rawRate: 0.0008m, nativeIntervalHours: 8, windowHours: 24m);

        // Hyperliquid: 1-hour native interval, RawRate = 0.0001 per settlement
        decimal hyperliquidAccrual = _sut.AccruedFunding(rawRate: 0.0001m, nativeIntervalHours: 1, windowHours: 24m);

        // Both should equal 0.0024 to 4 decimal places
        binanceAccrual.Should().BeApproximately(0.0024m, 0.00005m,
            "Binance: 0.0008 RawRate × 3 cycles (24h ÷ 8h) = 0.0024");
        hyperliquidAccrual.Should().BeApproximately(0.0024m, 0.00005m,
            "Hyperliquid: 0.0001 RawRate × 24 cycles (24h ÷ 1h) = 0.0024");
        binanceAccrual.Should().BeApproximately(hyperliquidAccrual, 0.00005m,
            "both legs must accrue equal amounts over the same 24h window");
    }

    [Theory]
    [InlineData(0.0008, 8, 24, 0.0024)]  // Binance 8h:      3 cycles
    [InlineData(0.0001, 1, 24, 0.0024)]  // Hyperliquid 1h: 24 cycles
    [InlineData(0.0001, 4, 8, 0.0002)]  // 4h interval:     2 cycles → 0.0001 × 2
    [InlineData(0.0003, 8, 8, 0.0003)]  // single cycle
    [InlineData(0.0005, 1, 0, 0.0000)]  // zero window
    public void AccruedFunding_UsesRawRateTimesNativeCycles(
        decimal rawRate, int nativeIntervalHours, decimal windowHours, decimal expected)
    {
        var result = _sut.AccruedFunding(rawRate, nativeIntervalHours, windowHours);
        result.Should().BeApproximately(expected, 0.00005m);
    }
}
