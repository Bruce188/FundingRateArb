using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class SignalEngineDenyListTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRates = new();
    private readonly Mock<IMarketDataCache> _mockCache = new();

    private static readonly BotConfiguration DefaultConfig = new()
    {
        OpenThreshold = 0.0001m,
        SlippageBufferBps = 0,
    };

    public SignalEngineDenyListTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);
    }

    private static FundingRateSnapshot MakeRate(int exchangeId, string exchangeName, int assetId, string symbol,
        decimal ratePerHour, decimal volume = 1_000_000m)
        => new()
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = ratePerHour,
            MarkPrice = 100m,
            Volume24hUsd = volume,
            RecordedAt = DateTime.UtcNow,
            Exchange = new Exchange { Id = exchangeId, Name = exchangeName },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };


    [Fact]
    public async Task DeniedPair_IsRejected_FromOpportunityList()
    {
        // Arrange: Hyperliquid/Aster pair is denied; ETH pair is Hyperliquid/Lighter — not in the deny list
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "BTC", 0.0001m),
            MakeRate(2, "Aster",       1, "BTC", 0.0050m), // large spread → would be opportunity
            MakeRate(1, "Hyperliquid", 2, "ETH", 0.0001m),
            MakeRate(3, "Lighter",     2, "ETH", 0.0050m), // not denied
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var snapshot = new Mock<IPairDenyListSnapshot>();
        snapshot.Setup(s => s.IsDenied(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        // ETH pair is Hyperliquid/Lighter — not in the deny list; BTC pair Hyperliquid/Aster is denied
        snapshot.Setup(s => s.IsDenied(
            It.Is<string>(l => l == "Hyperliquid"),
            It.Is<string>(r => r == "Aster")))
            .Returns(true);

        var provider = new Mock<IPairDenyListProvider>();
        provider.Setup(p => p.Current).Returns(snapshot.Object);

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, denyListProvider: provider.Object);

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: denied BTC pair absent, ETH pair present
        result.Opportunities.Should().NotContain(o => o.AssetSymbol == "BTC" && o.LongExchangeName == "Hyperliquid");
        result.Diagnostics!.PairsFilteredByDenyList.Should().BeGreaterThanOrEqualTo(1);
        result.Opportunities.Should().Contain(o => o.AssetSymbol == "ETH");
    }

    [Fact]
    public async Task DeniedPair_IsCaseInsensitive()
    {
        // Mock returns true for HYPERLIQUID/aster (mixed case) — provider uses OrdinalIgnoreCase
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "BTC", 0.0001m),
            MakeRate(2, "Aster",       1, "BTC", 0.0050m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var snapshot = new Mock<IPairDenyListSnapshot>();
        // Case-insensitive: IsDenied called with "Hyperliquid" and "Aster" after ordering
        snapshot.Setup(s => s.IsDenied(
            It.Is<string>(l => l != null && l.Equals("Hyperliquid", StringComparison.OrdinalIgnoreCase)),
            It.Is<string>(s2 => s2 != null && s2.Equals("Aster", StringComparison.OrdinalIgnoreCase))))
            .Returns(true);

        var provider = new Mock<IPairDenyListProvider>();
        provider.Setup(p => p.Current).Returns(snapshot.Object);

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, denyListProvider: provider.Object);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.PairsFilteredByDenyList.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task NoDenyListProvider_AllowsAllPairs()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "BTC", 0.0001m),
            MakeRate(2, "Aster",       1, "BTC", 0.0050m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // No denyListProvider injected
        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.PairsFilteredByDenyList.Should().Be(0);
    }

    [Fact]
    public async Task AllowedPair_RankedByFundingSpread_AsBefore()
    {
        // Two pairs, both allowed — ordering should match spread ranking (no provider vs empty-deny provider)
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "BTC", 0.0001m),
            MakeRate(2, "Aster",       1, "BTC", 0.0050m),
            MakeRate(1, "Hyperliquid", 2, "ETH", 0.0001m),
            MakeRate(2, "Aster",       2, "ETH", 0.0030m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var snapshot = new Mock<IPairDenyListSnapshot>();
        snapshot.Setup(s => s.IsDenied(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var provider = new Mock<IPairDenyListProvider>();
        provider.Setup(p => p.Current).Returns(snapshot.Object);

        var sutWithProvider = new SignalEngine(_mockUow.Object, _mockCache.Object, denyListProvider: provider.Object);
        var sutNoProvider = new SignalEngine(_mockUow.Object, _mockCache.Object);

        var withProvider = await sutWithProvider.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var noProvider = await sutNoProvider.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Same symbols, same count
        withProvider.Opportunities.Select(o => o.AssetSymbol).Should()
            .BeEquivalentTo(noProvider.Opportunities.Select(o => o.AssetSymbol));
        withProvider.Diagnostics!.PairsFilteredByDenyList.Should().Be(0);
    }
}
