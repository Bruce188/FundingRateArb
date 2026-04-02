using System.Text.Json;
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
    public void Cache_PreservesNormalizedRate()
    {
        // Verifies cache round-tripping preserves pre-computed normalized values
        var cache = new MarketDataCache();
        var dto = new FundingRateDto
        {
            ExchangeName = "Lighter",
            Symbol = "BTC",
            RawRate = 0.0003m,
            RatePerHour = 0.0000375m,
            MarkPrice = 50000m,
            IndexPrice = 50000m,
            Volume24hUsd = 500000m,
        };
        cache.Update(dto);

        var cached = cache.GetLatest("Lighter", "BTC");
        cached!.RatePerHour.Should().Be(0.0000375m);
        cached.RawRate.Should().Be(0.0003m);
    }

    [Fact]
    public void TryParseMarketStat_NormalizesEightHourRateToHourly()
    {
        // Exercises the actual WebSocket normalization path (fundingRate / 8m)
        var cache = new MarketDataCache();
        var stream = CreateStreamWithCache(cache);
        stream.SetMarketMapping(0, "ETH");

        var json = JsonDocument.Parse("""
            {
                "market_index": 0,
                "funding_rate_current": "0.0004",
                "index_price": "2100.50",
                "mark_price": "2101.00",
                "volume_24h": "15000000"
            }
            """);

        stream.TryParseMarketStat(json.RootElement);

        var cached = cache.GetLatest("Lighter", "ETH");
        cached.Should().NotBeNull();
        cached!.RawRate.Should().Be(0.0004m);
        cached.RatePerHour.Should().Be(0.00005m, "8-hour rate 0.0004 divided by 8 = 0.00005");
        cached.MarkPrice.Should().Be(2101.00m);
        cached.IndexPrice.Should().Be(2100.50m);
    }

    [Fact]
    public void TryParseMarketStat_ZeroRate_NormalizesToZero()
    {
        // Zero funding rate is a realistic production scenario (flat funding)
        var cache = new MarketDataCache();
        var stream = CreateStreamWithCache(cache);
        stream.SetMarketMapping(1, "BTC");

        var json = JsonDocument.Parse("""
            {
                "market_index": 1,
                "funding_rate_current": "0",
                "index_price": "69000.00",
                "mark_price": "69050.00",
                "volume_24h": "50000000"
            }
            """);

        stream.TryParseMarketStat(json.RootElement);

        var cached = cache.GetLatest("Lighter", "BTC");
        cached.Should().NotBeNull();
        cached!.RawRate.Should().Be(0m);
        cached.RatePerHour.Should().Be(0m);
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

    private static LighterMarketDataStream CreateStreamWithCache(MarketDataCache cache)
    {
        var wsClient = new LighterWebSocketClient(NullLogger<LighterWebSocketClient>.Instance);
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        mockHttpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        return new LighterMarketDataStream(
            wsClient, cache, mockHttpFactory.Object,
            NullLogger<LighterMarketDataStream>.Instance);
    }
}
