using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;

namespace FundingRateArb.Tests.Unit.Infrastructure;

/// <summary>
/// Tests for the parsing helper used by LighterConnector.GetPositionMarginStateAsync.
/// The full method is integration-tested against the live API; here we cover the load-bearing
/// decimal-string parsing that protects against locale-sensitivity bugs.
/// </summary>
public class LighterConnectorMarginStateTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    [InlineData("1", 1)]
    [InlineData("1.5", 1.5)]
    [InlineData("123.456", 123.456)]
    [InlineData("-1", -1)]
    [InlineData("-100.25", -100.25)]
    [InlineData("0.000001", 0.000001)]
    public void ParseDecimalOrZero_ValidInput_ReturnsCorrectValue(string? input, double expected)
    {
        var result = LighterConnector.ParseDecimalOrZero(input);
        result.Should().Be((decimal)expected);
    }

    [Fact]
    public void ParseDecimalOrZero_LargeValue_ParsesAtFullDecimalPrecision()
    {
        // Use decimal literal directly to avoid double precision loss in [InlineData]
        var result = LighterConnector.ParseDecimalOrZero("99999999.99999999");
        result.Should().Be(99999999.99999999m);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("not a number")]
    [InlineData("1.2.3")]
    [InlineData("$100")]
    [InlineData("100 USDC")]
    public void ParseDecimalOrZero_InvalidInput_ReturnsZero(string input)
    {
        var result = LighterConnector.ParseDecimalOrZero(input);
        result.Should().Be(0m);
    }

    [Fact]
    public void ParseDecimalOrZero_CommaDecimalSeparator_ReturnsZeroUnderStrictStyles()
    {
        // Lighter API returns plain dotted decimals. A comma-separated value would only appear
        // if a locale-sensitive caller mis-formatted the input or if an upstream proxy mangled it.
        // The strict NumberStyles (Float | AllowLeadingSign | AllowExponent) rejects thousands
        // separators outright — "1,5" parses as 0m. This is the load-bearing test for CWE-1284
        // defense: culture-sensitive parsing has caused production incidents before.
        var result = LighterConnector.ParseDecimalOrZero("1,5");

        result.Should().Be(0m,
            "strict NumberStyles rejects thousands separators to prevent 10x inflation bugs");
    }

    [Fact]
    public void ParseDecimalOrZero_HexNotation_ReturnsZero()
    {
        // NumberStyles.Any would accept "0x1A" as hex; the strict styles reject it.
        var result = LighterConnector.ParseDecimalOrZero("0x1A");
        result.Should().Be(0m);
    }

    [Fact]
    public void ParseDecimalOrZero_ParenthesesNegative_ReturnsZero()
    {
        // NumberStyles.Any accepts "(100)" as -100 (accounting convention);
        // the strict styles reject it. Upstream API uses explicit "-100" instead.
        var result = LighterConnector.ParseDecimalOrZero("(100)");
        result.Should().Be(0m);
    }

    [Fact]
    public void ParseDecimalOrZero_WhitespacePadding_HandlesGracefully()
    {
        var result = LighterConnector.ParseDecimalOrZero("  100.5  ");
        // NumberStyles.Any allows leading/trailing whitespace
        result.Should().Be(100.5m);
    }

    [Fact]
    public void ParseDecimalOrZero_ScientificNotation_Parses()
    {
        // Lighter occasionally returns very small numbers in scientific notation
        var result = LighterConnector.ParseDecimalOrZero("1.5e-3");
        result.Should().Be(0.0015m);
    }

    // ── ComputeMarginUtilization zero-denominator safeguard ────────────────

    [Theory]
    [InlineData(100, 50, 0.5)]       // 50% utilization
    [InlineData(100, 100, 1.0)]      // 100% utilization
    [InlineData(100, 0, 0.0)]        // idle account
    [InlineData(200, 150, 0.75)]     // 75% utilization
    public void ComputeMarginUtilization_NormalCase_ReturnsRatio(double accountValue, double marginUsed, double expected)
    {
        var result = LighterConnector.ComputeMarginUtilization((decimal)accountValue, (decimal)marginUsed);
        result.Should().Be((decimal)expected);
    }

    [Fact]
    public void ComputeMarginUtilization_ZeroAccountValueWithMargin_ReturnsFullUtilization()
    {
        // The catastrophic case: account value has collapsed to 0 while margin is
        // still committed. Reporting 0m (the old bug) would mask the emergency.
        var result = LighterConnector.ComputeMarginUtilization(0m, 50m);
        result.Should().Be(1m,
            "zero denominator with non-zero margin must report 100% so the alert fires");
    }

    [Fact]
    public void ComputeMarginUtilization_ZeroAccountValueZeroMargin_ReturnsZero()
    {
        // Legitimately empty account — no margin committed, report 0.
        var result = LighterConnector.ComputeMarginUtilization(0m, 0m);
        result.Should().Be(0m);
    }

    [Fact]
    public void ComputeMarginUtilization_NegativeAccountValue_TreatedAsZero()
    {
        // Defensive: if the API ever returns a negative totalAssetValue, treat as
        // zero-denominator. The `> 0` guard handles this.
        var result = LighterConnector.ComputeMarginUtilization(-10m, 50m);
        result.Should().Be(1m);
    }
}
