using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Tests for the SignalEngine spot short-leg filter.
/// A pair where the short leg is a Spot connector must be dropped from opportunities.
/// A pair where the long leg is Spot (short is Perp) must still be surfaced.
/// </summary>
public class SignalEngineSpotShortLegFilterTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IBotConfigRepository> _botConfig = new();
    private readonly Mock<IFundingRateRepository> _fundingRates = new();
    private readonly Mock<IMarketDataCache> _cache = new();
    private readonly Mock<IExchangeConnectorFactory> _factory = new();

    // Perp connector (default MarketType)
    private readonly Mock<IExchangeConnector> _perpConnector = new();
    // Spot connector
    private readonly Mock<IExchangeConnector> _spotConnector = new();

    public SignalEngineSpotShortLegFilterTests()
    {
        _uow.Setup(u => u.BotConfig).Returns(_botConfig.Object);
        _uow.Setup(u => u.FundingRates).Returns(_fundingRates.Object);

        _perpConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);
        _spotConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Spot);
    }

    private static FundingRateSnapshot MakeRate(
        int exchangeId, string exchangeName,
        int assetId, string symbol,
        decimal ratePerHour,
        decimal markPrice = 3000m,
        decimal volume = 1_000_000m) =>
        new()
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = ratePerHour,
            MarkPrice = markPrice,
            Volume24hUsd = volume,
            RecordedAt = DateTime.UtcNow,
            Exchange = new Exchange { Id = exchangeId, Name = exchangeName },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };

    private SignalEngine CreateEngine()
    {
        return new SignalEngine(
            _uow.Object,
            _cache.Object,
            connectorFactory: _factory.Object);
    }

    // ── Spot as short leg → pair dropped ────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_SpotIsShortLeg_PairDropped()
    {
        // LongR < ShortR → Lighter (low rate) is long, Binance (high rate) is short
        // If Binance is Spot → cannot short → drop pair
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Lighter", 1, "ETH", 0.0001m),   // low → long leg
            MakeRate(2, "Binance", 1, "ETH", 0.0010m),   // high → short leg
        };

        _botConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, SlippageBufferBps = 0 });
        _fundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        _factory.Setup(f => f.GetConnector("Lighter")).Returns(_perpConnector.Object);
        _factory.Setup(f => f.GetConnector("Binance")).Returns(_spotConnector.Object);

        var sut = CreateEngine();
        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().BeEmpty("short leg is Spot — cannot take short on a spot exchange");
    }

    // ── Spot as long leg → pair surfaces ────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_SpotIsLongLeg_PairSurfaced()
    {
        // Binance (low rate) = long leg, Lighter (high rate) = short leg (Perp)
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Binance",  1, "ETH", 0.0001m),   // low → long leg
            MakeRate(2, "Lighter",  1, "ETH", 0.0010m),   // high → short leg (Perp)
        };

        _botConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, SlippageBufferBps = 0 });
        _fundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        _factory.Setup(f => f.GetConnector("Binance")).Returns(_spotConnector.Object);
        _factory.Setup(f => f.GetConnector("Lighter")).Returns(_perpConnector.Object);

        var sut = CreateEngine();
        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        // The short leg (Lighter) is Perp → no filter should drop this pair
        result.Should().HaveCount(1, "long leg Spot + short leg Perp is a valid pair");
        result[0].LongExchangeName.Should().Be("Binance");
        result[0].ShortExchangeName.Should().Be("Lighter");
    }

    // ── Both perp → no filter applied ───────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_BothPerp_PairSurfaced()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        _botConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, SlippageBufferBps = 0 });
        _fundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        _factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(_perpConnector.Object);
        _factory.Setup(f => f.GetConnector("Lighter")).Returns(_perpConnector.Object);

        var sut = CreateEngine();
        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
    }
}
