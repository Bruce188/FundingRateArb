using FundingRateArb.Application.Services;
using FluentAssertions;

namespace FundingRateArb.Tests.Unit.Services;

public class PositionSizerRoundingTests
{
    [Theory]
    [InlineData(0.1234567, 0.00001, 5, 0.12345)]  // BTC on Hyperliquid
    [InlineData(1.5678,    0.0001,  4, 1.5678)]    // ETH on Hyperliquid (exact fit)
    [InlineData(1.56789,   0.0001,  4, 1.5678)]    // ETH on Hyperliquid (truncate)
    [InlineData(99.999,    0.01,    2, 99.99)]      // SOL on Hyperliquid
    [InlineData(0.004,     0.01,    2, 0.00)]       // Below minimum step
    [InlineData(50.0,      1.0,     0, 50.0)]       // Whole units only
    public void RoundToStepSize_AlwaysRoundsDown(
        decimal quantity, decimal stepSize, int decimals, decimal expected)
    {
        var result = PositionSizer.RoundToStepSize(quantity, stepSize, decimals);
        result.Should().Be(expected);
    }

    [Fact]
    public void RoundToStepSize_NeverRoundsUp()
    {
        // 1.999 with stepSize 0.01 should give 1.99, NOT 2.00
        var result = PositionSizer.RoundToStepSize(1.999m, 0.01m, 2);
        result.Should().Be(1.99m);
    }

    [Fact]
    public void RoundToStepSize_WithZeroStepSize_ReturnsRoundedToDecimals()
    {
        // stepSize = 0 is a guard — fall back to plain rounding
        var result = PositionSizer.RoundToStepSize(1.56789m, 0m, 4);
        result.Should().Be(1.5679m); // Math.Round, not floor
    }

    [Fact]
    public void RoundToStepSize_WithExactMultiple_ReturnsUnchanged()
    {
        var result = PositionSizer.RoundToStepSize(0.12345m, 0.00001m, 5);
        result.Should().Be(0.12345m);
    }
}
