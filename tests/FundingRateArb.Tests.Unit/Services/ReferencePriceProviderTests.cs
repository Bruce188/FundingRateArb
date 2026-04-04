using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.Services;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class ReferencePriceProviderTests
{
    private readonly Mock<IMarketDataCache> _mockCache = new();
    private readonly ReferencePriceProvider _sut;

    public ReferencePriceProviderTests()
    {
        _sut = new ReferencePriceProvider(_mockCache.Object);
    }

    [Fact]
    public void GetUnifiedPrice_WithBinanceLong_UsesBinanceIndexPrice()
    {
        _mockCache.Setup(c => c.GetLatest("Binance", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Binance", Symbol = "ETH", IndexPrice = 3000m, MarkPrice = 3001m });
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Lighter", Symbol = "ETH", IndexPrice = 2999m, MarkPrice = 2998m });

        var result = _sut.GetUnifiedPrice("ETH", "Binance", "Lighter");

        result.Should().Be(3000m);
    }

    [Fact]
    public void GetUnifiedPrice_WithBinanceShort_UsesBinanceIndexPrice()
    {
        _mockCache.Setup(c => c.GetLatest("Hyperliquid", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Hyperliquid", Symbol = "ETH", IndexPrice = 2999m, MarkPrice = 2998m });
        _mockCache.Setup(c => c.GetLatest("Binance", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Binance", Symbol = "ETH", IndexPrice = 3000m, MarkPrice = 3001m });

        var result = _sut.GetUnifiedPrice("ETH", "Hyperliquid", "Binance");

        result.Should().Be(3000m);
    }

    [Fact]
    public void GetUnifiedPrice_BothDex_UsesAverageIndexPrice()
    {
        _mockCache.Setup(c => c.GetLatest("Hyperliquid", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Hyperliquid", Symbol = "ETH", IndexPrice = 3000m, MarkPrice = 3005m });
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Lighter", Symbol = "ETH", IndexPrice = 3002m, MarkPrice = 2998m });

        var result = _sut.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter");

        result.Should().Be(3001m);
    }

    [Fact]
    public void GetUnifiedPrice_ZeroIndexPrices_FallsBackToMarkPriceAverage()
    {
        _mockCache.Setup(c => c.GetLatest("Hyperliquid", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Hyperliquid", Symbol = "ETH", IndexPrice = 0m, MarkPrice = 3000m });
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Lighter", Symbol = "ETH", IndexPrice = 0m, MarkPrice = 3004m });

        var result = _sut.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter");

        result.Should().Be(3002m);
    }

    [Fact]
    public void GetUnifiedPrice_NullDtos_ReturnsZero()
    {
        _mockCache.Setup(c => c.GetLatest(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((FundingRateDto?)null);

        var result = _sut.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter");

        result.Should().Be(0m);
    }

    [Fact]
    public void GetUnifiedPrice_LongDtoNull_ShortHasData_UsesShortMarkPrice()
    {
        _mockCache.Setup(c => c.GetLatest("Hyperliquid", "ETH"))
            .Returns((FundingRateDto?)null);
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Lighter", Symbol = "ETH", IndexPrice = 3000m, MarkPrice = 2998m });

        var result = _sut.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter");

        result.Should().Be(2998m);
    }

    [Fact]
    public void GetUnifiedPrice_ShortDtoNull_LongHasData_UsesLongMarkPrice()
    {
        _mockCache.Setup(c => c.GetLatest("Hyperliquid", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Hyperliquid", Symbol = "ETH", IndexPrice = 3000m, MarkPrice = 3005m });
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns((FundingRateDto?)null);

        var result = _sut.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter");

        result.Should().Be(3005m);
    }

    [Fact]
    public void GetUnifiedPrice_BinanceWithZeroIndexPrice_FallsThroughToNextTier()
    {
        _mockCache.Setup(c => c.GetLatest("Binance", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Binance", Symbol = "ETH", IndexPrice = 0m, MarkPrice = 3001m });
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Lighter", Symbol = "ETH", IndexPrice = 0m, MarkPrice = 3003m });

        var result = _sut.GetUnifiedPrice("ETH", "Binance", "Lighter");

        // Both index prices are 0, falls through to mark price average
        result.Should().Be(3002m);
    }

    [Fact]
    public void GetUnifiedPrice_CaseInsensitiveBinanceMatch()
    {
        _mockCache.Setup(c => c.GetLatest("binance", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "binance", Symbol = "ETH", IndexPrice = 3000m, MarkPrice = 3001m });
        _mockCache.Setup(c => c.GetLatest("Lighter", "ETH"))
            .Returns(new FundingRateDto { ExchangeName = "Lighter", Symbol = "ETH", IndexPrice = 2999m, MarkPrice = 2998m });

        var result = _sut.GetUnifiedPrice("ETH", "binance", "Lighter");

        result.Should().Be(3000m);
    }
}
