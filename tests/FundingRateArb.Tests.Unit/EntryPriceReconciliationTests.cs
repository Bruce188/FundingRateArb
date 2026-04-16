using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit;

/// <summary>
/// End-to-end tests covering the entry price reconciliation flow:
/// order placement with estimated fill -> verification -> reconciliation -> correct entry prices persisted -> accurate PnL.
/// </summary>
public class EntryPriceReconciliationTests
{
    // ── Shared setup ─────────────────────────────────────────────────

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IPnlReconciliationService> _mockReconciliation = new();

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        UpdatedByUserId = "admin-user-id",
        StopLossPct = 0.15m,
        CloseThreshold = -0.00005m,
        AlertThreshold = 0.0001m,
        MaxHoldTimeHours = 72,
        AdaptiveHoldEnabled = true,
    };

    private static readonly ArbitrageOpportunityDto DefaultOpp = new()
    {
        AssetSymbol = "ETH",
        AssetId = 1,
        LongExchangeName = "Hyperliquid",
        LongExchangeId = 1,
        ShortExchangeName = "Lighter",
        ShortExchangeId = 2,
        SpreadPerHour = 0.0005m,
        NetYieldPerHour = 0.0004m,
        LongMarkPrice = 3000m,
        ShortMarkPrice = 3001m,
    };

    private Mock<IExchangeConnector> CreateReconcilableShortConnector(decimal? reconcilePrice = 2999.50m)
    {
        var mock = new Mock<IExchangeConnector>();
        mock.As<IEntryPriceReconcilable>();
        mock.Setup(c => c.ExchangeName).Returns("Lighter");
        mock.Setup(c => c.IsEstimatedFillExchange).Returns(false);
        mock.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mock.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        mock.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);
        mock.Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });
        mock.As<IEntryPriceReconcilable>()
            .Setup(r => r.GetActualEntryPriceAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reconcilePrice);
        return mock;
    }

    private ExecutionEngine CreateExecutionEngine()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);

        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));
        _mockUserSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5 });

        _mockLongConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockLongConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        _mockLongConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _mockLongConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);
        _mockLongConnector.Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "long-1", FilledPrice = 3000m, FilledQuantity = 0.1m });

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);

        var connectorLifecycle = new ConnectorLifecycleManager(
            _mockFactory.Object, _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            NullLogger<ConnectorLifecycleManager>.Instance);
        var emergencyClose = new EmergencyCloseHandler(
            _mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);
        var positionCloser = new PositionCloser(
            _mockUow.Object, connectorLifecycle, _mockReconciliation.Object, NullLogger<PositionCloser>.Instance);

        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { Balances = new List<ExchangeBalanceDto>(), TotalAvailableUsdc = 1000m, FetchedAt = DateTime.UtcNow });
        return new ExecutionEngine(_mockUow.Object, connectorLifecycle, emergencyClose, positionCloser,
            _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            mockBalanceAggregator.Object,
            NullLogger<ExecutionEngine>.Instance);
    }

    // ── Scenario 1: Reconcilable connector returns actual price ──────

    [Fact]
    public async Task ReconciledFlow_UpdatesEntryPriceBeforeSave()
    {
        var mockShort = CreateReconcilableShortConnector(2999.50m);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockShort.Object);

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var sut = CreateExecutionEngine();
        var result = await sut.OpenPositionAsync("test-user", DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        addedPosition.Should().NotBeNull();
        addedPosition!.ShortEntryPrice.Should().Be(2999.50m,
            "reconciled price should replace estimated fill before persistence");
        addedPosition.LongEntryPrice.Should().Be(3000m,
            "non-estimated long leg should keep original price");
    }

    // ── Scenario 2: Non-reconcilable connector keeps estimated price ──

    [Fact]
    public async Task NonReconcilableConnector_KeepsEstimatedPrice()
    {
        // Default mock short connector does NOT implement IEntryPriceReconcilable
        var mockShort = new Mock<IExchangeConnector>();
        mockShort.Setup(c => c.ExchangeName).Returns("Lighter");
        mockShort.Setup(c => c.IsEstimatedFillExchange).Returns(false);
        mockShort.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mockShort.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        mockShort.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);
        mockShort.Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockShort.Object);

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var sut = CreateExecutionEngine();
        var result = await sut.OpenPositionAsync("test-user", DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        addedPosition.Should().NotBeNull();
        addedPosition!.ShortEntryPrice.Should().Be(3001m,
            "non-reconcilable connector should retain estimated fill price");
    }

    // ── Scenario 3: Reconciliation failure (returns null) keeps estimated ──

    [Fact]
    public async Task ReconciliationFailure_KeepsEstimatedPrice_WithWarningLog()
    {
        var mockShort = CreateReconcilableShortConnector(reconcilePrice: null);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockShort.Object);

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var sut = CreateExecutionEngine();
        var result = await sut.OpenPositionAsync("test-user", DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        addedPosition.Should().NotBeNull();
        addedPosition!.ShortEntryPrice.Should().Be(3001m,
            "null reconciliation should keep estimated fill price");
    }

    // ── Scenario 4: PnL with reconciled prices and filled quantities ──

    [Fact]
    public async Task PnL_WithReconciledPricesAndFilledQuantities_UsesPerLegValues()
    {
        // Set up a position with reconciled entry prices and per-leg filled quantities
        var pos = new ArbitragePosition
        {
            Id = 1,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 100m,
            Leverage = 5,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 2999.50m, // reconciled price
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0005m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongFilledQuantity = 0.10m,
            ShortFilledQuantity = 0.10m,
        };

        var mockPositions = new Mock<IPositionRepository>();
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        mockPositions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>())).ReturnsAsync([]);

        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.BotConfig).Returns(Mock.Of<IBotConfigRepository>(b => b.GetActiveAsync() == Task.FromResult(DefaultConfig)));
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.Alerts).Returns(Mock.Of<IAlertRepository>(a =>
            a.GetRecentByPositionIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>())
            == Task.FromResult(new Dictionary<(int PositionId, AlertType Type), Alert>())));
        mockUow.Setup(u => u.FundingRates).Returns(Mock.Of<IFundingRateRepository>(f =>
            f.GetLatestPerExchangePerAssetAsync() == Task.FromResult(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m, MarkPrice = 3010m, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
                new() { ExchangeId = 2, AssetId = 1, RatePerHour = 0.0006m, MarkPrice = 3010m, Exchange = new Exchange { Id = 2, Name = "Lighter" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
            })));
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mockConnFactory = new Mock<IExchangeConnectorFactory>();
        var longConn = new Mock<IExchangeConnector>();
        var shortConn = new Mock<IExchangeConnector>();
        longConn.Setup(c => c.HasCredentials).Returns(true);
        shortConn.Setup(c => c.HasCredentials).Returns(true);
        longConn.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3010m);
        shortConn.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3010m);
        mockConnFactory.Setup(f => f.GetConnector("Hyperliquid")).Returns(longConn.Object);
        mockConnFactory.Setup(f => f.GetConnector("Lighter")).Returns(shortConn.Object);

        var mockReferencePriceProvider = new Mock<IReferencePriceProvider>();
        mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0m); // fallback to per-exchange PnL

        var sut = new PositionHealthMonitor(mockUow.Object,
            mockConnFactory.Object, new Mock<IMarketDataCache>().Object, mockReferencePriceProvider.Object,
            Mock.Of<IExecutionEngine>(), Mock.Of<ILeverageTierProvider>(), new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var result = await sut.CheckAndActAsync();

        // With per-leg quantities (0.10 each) and reconciled prices:
        // longPnl  = (3010 - 3000) * 0.10 = 1.0
        // shortPnl = (2999.50 - 3010) * 0.10 = -1.05
        // unrealizedPnl = -0.05 (small loss, well within stop-loss threshold of 15)
        // Should NOT close the position
        result.ToClose.Should().BeEmpty("small net PnL should not trigger stop-loss");
    }

    // ── Scenario 5: PnL fallback with null filled quantities ──

    [Fact]
    public async Task PnL_Fallback_WhenFilledQuantitiesNull_UsesEstimatedQuantity()
    {
        var pos = new ArbitragePosition
        {
            Id = 1,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 100m,
            Leverage = 5,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0005m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongFilledQuantity = null,
            ShortFilledQuantity = null,
        };

        var mockPositions = new Mock<IPositionRepository>();
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        mockPositions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>())).ReturnsAsync([]);

        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.BotConfig).Returns(Mock.Of<IBotConfigRepository>(b => b.GetActiveAsync() == Task.FromResult(DefaultConfig)));
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.Alerts).Returns(Mock.Of<IAlertRepository>(a =>
            a.GetRecentByPositionIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>())
            == Task.FromResult(new Dictionary<(int PositionId, AlertType Type), Alert>())));
        mockUow.Setup(u => u.FundingRates).Returns(Mock.Of<IFundingRateRepository>(f =>
            f.GetLatestPerExchangePerAssetAsync() == Task.FromResult(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m, MarkPrice = 2500m, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
                new() { ExchangeId = 2, AssetId = 1, RatePerHour = 0.0006m, MarkPrice = 3001m, Exchange = new Exchange { Id = 2, Name = "Lighter" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
            })));
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mockConnFactory = new Mock<IExchangeConnectorFactory>();
        var longConn = new Mock<IExchangeConnector>();
        var shortConn = new Mock<IExchangeConnector>();
        longConn.Setup(c => c.HasCredentials).Returns(true);
        shortConn.Setup(c => c.HasCredentials).Returns(true);
        longConn.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(2500m);
        shortConn.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>())).ReturnsAsync(3001m);
        mockConnFactory.Setup(f => f.GetConnector("Hyperliquid")).Returns(longConn.Object);
        mockConnFactory.Setup(f => f.GetConnector("Lighter")).Returns(shortConn.Object);

        var mockReferencePriceProvider = new Mock<IReferencePriceProvider>();
        mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0m);

        var sut = new PositionHealthMonitor(mockUow.Object,
            mockConnFactory.Object, new Mock<IMarketDataCache>().Object, mockReferencePriceProvider.Object,
            Mock.Of<IExecutionEngine>(), Mock.Of<ILeverageTierProvider>(), new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var result = await sut.CheckAndActAsync();

        // With estimated qty (null filled quantities):
        // avgEntryPrice = (3000+3001)/2 = 3000.5, estimatedQty = 500/3000.5 ~ 0.16664
        // longPnl  = (2500-3000) * 0.16664 ~ -83.32
        // shortPnl = (3001-3001) * 0.16664 = 0
        // unrealizedPnl ~ -83.32 → |83.32| > 15 → triggers stop-loss
        result.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.StopLoss);
    }
}
