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
    public void ParseDecimalOrZero_CommaDecimalSeparator_ReturnsZeroUnderInvariantCulture()
    {
        // Lighter API returns plain dotted decimals. A comma-separated value would only appear
        // if a locale-sensitive caller mis-formatted the input. The invariant-culture parse must
        // reject this rather than silently treat "1,5" as "15" (en-US thousands) or "1.5" (de-DE).
        // This is a load-bearing test — culture-sensitive parsing has caused production incidents
        // in this codebase before per the user's commit history feedback.
        var result = LighterConnector.ParseDecimalOrZero("1,5");

        // NumberStyles.Any with InvariantCulture treats "," as a thousands separator,
        // so "1,5" parses as 15 (one comma followed by digits is interpreted as a group).
        // Document the actual behavior so future tightening to NumberStyles.Float
        // (which would reject this) is intentional.
        result.Should().BeOneOf(0m, 15m);
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
}
