using FluentAssertions;
using FundingRateArb.Application.Common;

namespace FundingRateArb.Tests.Unit.Common;

public class ExchangeFeeConstantsTests
{
    [Theory]
    [InlineData("Hyperliquid", 0.00045)]
    [InlineData("Lighter", 0)]
    [InlineData("Aster", 0.0004)]
    public void KnownExchanges_ReturnExpectedRates(string exchangeName, decimal expectedRate)
    {
        ExchangeFeeConstants.GetTakerFeeRate(exchangeName).Should().Be(expectedRate);
    }

    [Theory]
    [InlineData("UnknownExchange")]
    [InlineData("Binance")]
    public void UnknownExchange_ReturnsDefault(string exchangeName)
    {
        ExchangeFeeConstants.GetTakerFeeRate(exchangeName).Should().Be(0.0005m);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyOrWhitespace_ReturnsDefault(string exchangeName)
    {
        ExchangeFeeConstants.GetTakerFeeRate(exchangeName).Should().Be(0.0005m);
    }
}
