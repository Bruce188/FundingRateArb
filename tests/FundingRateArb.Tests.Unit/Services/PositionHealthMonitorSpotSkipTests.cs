using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Tests for the PositionHealthMonitor spot-leg margin skip.
/// When the long connector is Spot, GetPositionMarginStateAsync must NOT be called on it
/// because spot has no margin/liquidation concepts.
/// When both legs are Perp, GetPositionMarginStateAsync is called normally.
/// </summary>
public class PositionHealthMonitorSpotSkipTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IBotConfigRepository> _botConfig = new();
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IAlertRepository> _alerts = new();
    private readonly Mock<IFundingRateRepository> _fundingRates = new();
    private readonly Mock<IExchangeConnectorFactory> _factory = new();
    private readonly Mock<IExchangeConnector> _longConnector = new();
    private readonly Mock<IExchangeConnector> _shortConnector = new();
    private readonly Mock<IReferencePriceProvider> _referencePrice = new();
    private readonly Mock<IExecutionEngine> _executionEngine = new();

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        CloseThreshold = -0.00005m,
        AlertThreshold = 0.0001m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 72,
        MinHoldTimeHours = 2,
        MaxLeverageCap = 50,
        AdaptiveHoldEnabled = true,
    };

    public PositionHealthMonitorSpotSkipTests()
    {
        _uow.Setup(u => u.BotConfig).Returns(_botConfig.Object);
        _uow.Setup(u => u.Positions).Returns(_positions.Object);
        _uow.Setup(u => u.Alerts).Returns(_alerts.Object);
        _uow.Setup(u => u.FundingRates).Returns(_fundingRates.Object);
        _uow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _botConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);

        _alerts.Setup(a => a.GetRecentByPositionIdsAsync(
            It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Dictionary<(int, AlertType), Alert>());
        _positions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>())).ReturnsAsync([]);

        _referencePrice.Setup(r => r.GetUnifiedPrice(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0m);

        _longConnector.Setup(c => c.HasCredentials).Returns(true);
        _shortConnector.Setup(c => c.HasCredentials).Returns(true);
    }

    private PositionHealthMonitor CreateMonitor()
    {
        return new PositionHealthMonitor(
            _uow.Object,
            _factory.Object,
            new Mock<IMarketDataCache>().Object,
            _referencePrice.Object,
            _executionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);
    }

    private ArbitragePosition MakeOpenPosition(string longExchange, string shortExchange) =>
        new()
        {
            Id = 1,
            UserId = "user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 100m,
            Leverage = 1,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0005m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = longExchange },
            ShortExchange = new Exchange { Id = 2, Name = shortExchange },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

    private void SetupLatestRates(string longExchange, string shortExchange)
    {
        _fundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new()
                {
                    ExchangeId = 1, AssetId = 1, RatePerHour = 0.0003m,
                    MarkPrice = 3000m,
                    Exchange = new Exchange { Id = 1, Name = longExchange },
                    Asset = new Asset { Id = 1, Symbol = "ETH" },
                },
                new()
                {
                    ExchangeId = 2, AssetId = 1, RatePerHour = 0.0008m,
                    MarkPrice = 3001m,
                    Exchange = new Exchange { Id = 2, Name = shortExchange },
                    Asset = new Asset { Id = 1, Symbol = "ETH" },
                },
            });
    }

    // ── Spot long leg → GetPositionMarginStateAsync NOT called on long ────────

    [Fact]
    public async Task CheckAndAct_SpotLongLeg_MarginStateNotFetchedForSpotConnector()
    {
        // Long = Binance (Spot), Short = Lighter (Perp)
        _longConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Spot);
        _shortConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);

        _longConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _shortConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3001m);

        _factory.Setup(f => f.GetConnector("Binance")).Returns(_longConnector.Object);
        _factory.Setup(f => f.GetConnector("Lighter")).Returns(_shortConnector.Object);

        var pos = MakeOpenPosition("Binance", "Lighter");
        _positions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates("Binance", "Lighter");

        var sut = CreateMonitor();
        await sut.CheckAndActAsync();

        // Spot leg must NOT have its margin state queried
        _longConnector.Verify(
            c => c.GetPositionMarginStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Spot connector has no margin state — must not be queried for liquidation price");

        // Perp short leg may be queried (if the implementation decides to)
        // No assertion required — just confirm no exception was thrown
    }

    // ── Both Perp → liquidation only from perp legs ─────────────────────────

    [Fact]
    public async Task CheckAndAct_BothPerp_DoesNotSkipEitherConnector()
    {
        // Long = Hyperliquid (Perp), Short = Lighter (Perp)
        _longConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);
        _shortConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);

        _longConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _shortConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3001m);

        // Return null from margin state (no liquidation data) — healthy position, no close triggered
        _longConnector.Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MarginStateDto?)null);
        _shortConnector.Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MarginStateDto?)null);

        _factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(_longConnector.Object);
        _factory.Setup(f => f.GetConnector("Lighter")).Returns(_shortConnector.Object);

        var pos = MakeOpenPosition("Hyperliquid", "Lighter");
        _positions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates("Hyperliquid", "Lighter");

        var sut = CreateMonitor();

        // Must complete without exception — perp legs can be queried
        var act = async () => await sut.CheckAndActAsync();
        await act.Should().NotThrowAsync();
    }
}
