using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;

namespace FundingRateArb.Tests.Unit.Infrastructure;

/// <summary>
/// Tests for the testable helpers in HyperliquidConnector's margin state pipeline.
/// The SDK-dependent GetPositionMarginStateAsync path itself is covered by integration
/// tests against a live testnet — unit mocking the full HyperLiquid.Net pipeline is
/// too brittle to justify.
/// </summary>
public class HyperliquidConnectorMarginStateTests
{
    [Theory]
    [InlineData(100, 50, 0.5)]
    [InlineData(100, 100, 1.0)]
    [InlineData(100, 0, 0.0)]
    [InlineData(200, 150, 0.75)]
    public void ComputeMarginUtilization_NormalCase_ReturnsRatio(double accountValue, double totalMarginUsed, double expected)
    {
        var result = HyperliquidConnector.ComputeMarginUtilization((decimal)accountValue, (decimal)totalMarginUsed);
        result.Should().Be((decimal)expected);
    }

    [Fact]
    public void ComputeMarginUtilization_ZeroAccountValueWithMargin_ReturnsFullUtilization()
    {
        // The catastrophic case: cross-margin account collapsed to 0 while positions
        // still have margin committed. The previous bug returned 0% here.
        var result = HyperliquidConnector.ComputeMarginUtilization(0m, 50m);
        result.Should().Be(1m,
            "zero accountValue with non-zero margin must report 100% so the alert fires");
    }

    [Fact]
    public void ComputeMarginUtilization_ZeroAccountValueZeroMargin_ReturnsZero()
    {
        // Empty account with no margin — legitimately idle, report 0.
        var result = HyperliquidConnector.ComputeMarginUtilization(0m, 0m);
        result.Should().Be(0m);
    }

    [Fact]
    public void ComputeMarginUtilization_NegativeAccountValue_TreatedAsZero()
    {
        // Defense against the SDK ever returning a negative account value.
        var result = HyperliquidConnector.ComputeMarginUtilization(-10m, 50m);
        result.Should().Be(1m);
    }
}
