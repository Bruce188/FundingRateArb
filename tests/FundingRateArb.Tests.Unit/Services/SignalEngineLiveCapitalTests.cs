using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class SignalEngineLiveCapitalTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRates = new();
    private readonly Mock<IMarketDataCache> _mockCache = new();
    private readonly Mock<IUserConfigurationRepository> _mockUserConfigs = new();

    public SignalEngineLiveCapitalTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);
        _mockUserConfigs.Setup(r => r.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { "user1" });
    }

    private static FundingRateSnapshot MakeRate(
        int exchangeId, string exchangeName,
        int assetId, string symbol,
        decimal ratePerHour,
        decimal volume = 1_000_000m) =>
        new FundingRateSnapshot
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = ratePerHour,
            MarkPrice = 3000m,
            Volume24hUsd = volume,
            RecordedAt = DateTime.UtcNow,
            Exchange = new Exchange { Id = exchangeId, Name = exchangeName, FundingIntervalHours = 1 },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };

    private static IBalanceAggregator MakeAggregator(BalanceSnapshotDto snapshot)
    {
        var mock = new Mock<IBalanceAggregator>();
        mock.Setup(a => a.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        return mock.Object;
    }

    private static BalanceSnapshotDto SingleExchangeSnapshot(decimal availableUsdc) =>
        new BalanceSnapshotDto
        {
            Balances =
            [
                new ExchangeBalanceDto
                {
                    ExchangeId = 99, ExchangeName = "TestExchange",
                    AvailableUsdc = availableUsdc,
                }
            ]
        };

    [Fact]
    public async Task RefNotional_AtLine395_UsesLiveCapitalNotConfigField()
    {
        // Arrange: live capital = 9000, config TotalCapitalUsdc = 1000 (intentionally different)
        var tierProvider = new Mock<ILeverageTierProvider>();
        decimal capturedNotional = -1m;
        tierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Callback<string, string, decimal>((_, _, n) => capturedNotional = n)
            .Returns(int.MaxValue);

        var balanceAggregator = MakeAggregator(SingleExchangeSnapshot(9_000m));

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object,
            tierProvider: tierProvider.Object, balanceAggregator: balanceAggregator);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 5,
            MaxLeverageCap = 50,
#pragma warning disable CS0618
            TotalCapitalUsdc = 1_000m,
#pragma warning restore CS0618
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            });

        // Act
        await sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: refNotional = liveCapital(9000) * MaxCapPerPos(0.5) * cappedLev(5) = 22500
        // NOT config-based: 1000 * 0.5 * 5 = 2500
        capturedNotional.Should().Be(9_000m * 0.50m * 5m);
    }

    [Fact]
    public async Task LeverageCapBranch_AtLine456_UsesLiveCapital()
    {
        // Arrange: live capital = 8000, config = 500.
        // Cap on Aster = 10_000. sizedNotional = liveCapital * maxPerPos * lev.
        // With live capital:  8000 * 0.9 * 3 = 21600 > 10000 → filtered (opportunity absent)
        // With config capital: 500 * 0.9 * 3 = 1350  < 10000 → NOT filtered (would pass)
        var symbolConstraints = new Mock<IExchangeSymbolConstraintsProvider>();
        symbolConstraints
            .Setup(p => p.GetMaxNotionalAsync("Aster", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10_000m);
        symbolConstraints
            .Setup(p => p.GetMaxNotionalAsync("Lighter", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);

        var balanceAggregator = MakeAggregator(SingleExchangeSnapshot(8_000m));

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object,
            symbolConstraintsProvider: symbolConstraints.Object,
            balanceAggregator: balanceAggregator);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 10,
#pragma warning disable CS0618
            TotalCapitalUsdc = 500m,
#pragma warning restore CS0618
            MaxCapitalPerPosition = 0.90m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Aster",   1, "ETH", 0.0001m),
                MakeRate(2, "Lighter", 1, "ETH", 0.0010m),
            });

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: live capital drives sizedNotional above cap → filtered
        result.Opportunities.Should().BeEmpty("live capital exceeds Aster notional cap");
        result.Diagnostics!.PairsFilteredByExchangeSymbolCap.Should().Be(1);
    }

    [Fact]
    public async Task UnavailableExchange_ExcludedFromBase()
    {
        // Arrange: two exchanges, one unavailable. Capital base should only include available.
        var tierProvider = new Mock<ILeverageTierProvider>();
        decimal capturedNotional = -1m;
        tierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Callback<string, string, decimal>((_, _, n) => capturedNotional = n)
            .Returns(int.MaxValue);

        var balanceAggregator = MakeAggregator(new BalanceSnapshotDto
        {
            Balances =
            [
                new ExchangeBalanceDto
                {
                    ExchangeId = 1, ExchangeName = "Exchange1",
                    AvailableUsdc = 5_000m, IsUnavailable = false,
                },
                new ExchangeBalanceDto
                {
                    ExchangeId = 2, ExchangeName = "Exchange2",
                    AvailableUsdc = 3_000m, IsUnavailable = true, // excluded
                },
            ]
        });

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object,
            tierProvider: tierProvider.Object, balanceAggregator: balanceAggregator);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 4,
            MaxLeverageCap = 10,
#pragma warning disable CS0618
            TotalCapitalUsdc = 10_000m,
#pragma warning restore CS0618
            MaxCapitalPerPosition = 1.0m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            });

        // Act
        await sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: only available exchange contributes → liveCapital = 5000
        // refNotional = 5000 * 1.0 * 4 = 20000 (not 8000 = (5000+3000) * 1.0 * 4)
        capturedNotional.Should().Be(5_000m * 1.0m * 4m);
    }

    // --- ICapitalProvider wiring tests (Task 4.1 stubs the provider directly) ---

    private static ICapitalProvider MakeCapitalProvider(decimal evaluatedCapital)
    {
        var mock = new Mock<ICapitalProvider>();
        mock.Setup(p => p.GetEvaluatedCapitalUsdcAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(evaluatedCapital);
        return mock.Object;
    }

    [Fact]
    public async Task WithCapitalProvider_RefNotionalUsesProviderValue_NotAggregator()
    {
        // Provider returns 56 (live=$56, cap=$500 capped). Aggregator is irrelevant when provider is wired.
        var tierProvider = new Mock<ILeverageTierProvider>();
        decimal capturedNotional = -1m;
        tierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Callback<string, string, decimal>((_, _, n) => capturedNotional = n)
            .Returns(int.MaxValue);

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object,
            tierProvider: tierProvider.Object,
            balanceAggregator: MakeAggregator(SingleExchangeSnapshot(9_999m)),
            capitalProvider: MakeCapitalProvider(56m));

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 5,
            MaxLeverageCap = 50,
#pragma warning disable CS0618
            TotalCapitalUsdc = 500m,
#pragma warning restore CS0618
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            });

        await sut.GetOpportunitiesAsync(CancellationToken.None);

        // refNotional = providerValue(56) * MaxCapPerPos(0.5) * lev(5) = 140
        // NOT aggregator-derived (would be 9999 * 0.5 * 5 = 24997.5)
        capturedNotional.Should().Be(56m * 0.50m * 5m);
    }

    [Fact]
    public async Task WithCapitalProvider_DiagnosticsEvaluatedCapitalUsdcMatchesProvider()
    {
        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object,
            balanceAggregator: MakeAggregator(SingleExchangeSnapshot(0m)),
            capitalProvider: MakeCapitalProvider(56m));

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 10,
#pragma warning disable CS0618
            TotalCapitalUsdc = 500m,
#pragma warning restore CS0618
            MaxCapitalPerPosition = 1.0m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            });

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.EvaluatedCapitalUsdc.Should().Be(56m);
    }
}
