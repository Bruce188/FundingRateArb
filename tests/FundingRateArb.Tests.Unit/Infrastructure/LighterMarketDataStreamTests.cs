using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class LighterMarketDataStreamTests
{
    [Fact]
    public void ExchangeName_IsLighter()
    {
        var stream = CreateStream(out _, out _);
        stream.ExchangeName.Should().Be("Lighter");
    }

    [Fact]
    public void IsConnected_DelegatesToWebSocketClient()
    {
        var stream = CreateStream(out _, out _);
        // Before connect, should be false
        stream.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void FundingRate_UsedAsIs_NoConversion()
    {
        // Lighter funding rate is already per-hour, same as RawRate
        var cache = new MarketDataCache();
        var dto = new FundingRateDto
        {
            ExchangeName = "Lighter",
            Symbol = "BTC",
            RawRate = 0.0003m,
            RatePerHour = 0.0003m, // No division
            MarkPrice = 50000m,
            IndexPrice = 50000m,
            Volume24hUsd = 500000m,
        };
        cache.Update(dto);

        var cached = cache.GetLatest("Lighter", "BTC");
        cached!.RatePerHour.Should().Be(0.0003m);
        cached.RawRate.Should().Be(cached.RatePerHour);
    }

    private static LighterMarketDataStream CreateStream(
        out Mock<IMarketDataCache> mockCache, out LighterWebSocketClient wsClient)
    {
        wsClient = new LighterWebSocketClient(NullLogger<LighterWebSocketClient>.Instance);
        mockCache = new Mock<IMarketDataCache>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        mockHttpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        return new LighterMarketDataStream(
            wsClient, mockCache.Object, mockHttpFactory.Object,
            NullLogger<LighterMarketDataStream>.Instance);
    }
}
