using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Unit tests for all order placement reliability improvements (plan-v50):
/// - ExecutionEngine zero entry price guard
/// - ExecutionEngine one-sided close retry with leg flags
/// - PositionHealthMonitor zero-price escalation
/// </summary>
public class OrderPlacementReliabilityTests
{
    // ── ExecutionEngine test infrastructure ──

    private const string TestUserId = "test-user-id";

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IExchangeConnector> _mockShortConnector = new();
    private readonly Mock<IPnlReconciliationService> _mockReconciliation = new();
    private readonly ExecutionEngine _engine;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        UpdatedByUserId = "admin-user-id",
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 72,
        CloseThreshold = -0.00005m,
        AlertThreshold = 0.0001m,
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

    public OrderPlacementReliabilityTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => p.Id = 1);

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

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        _mockLongConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockShortConnector.Setup(c => c.ExchangeName).Returns("Lighter");

        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);

        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);
        _mockShortConnector
            .Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);
        _mockLongConnector
            .Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6);
        _mockShortConnector
            .Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6);
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        var connectorLifecycle = new ConnectorLifecycleManager(
            _mockFactory.Object, _mockUserSettings.Object, Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            NullLogger<ConnectorLifecycleManager>.Instance);
        var emergencyClose = new EmergencyCloseHandler(
            _mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);
        var positionCloser = new PositionCloser(
            _mockUow.Object, connectorLifecycle, _mockReconciliation.Object, NullLogger<PositionCloser>.Instance);

        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { Balances = new List<ExchangeBalanceDto>(), TotalAvailableUsdc = 1000m, FetchedAt = DateTime.UtcNow });
        _engine = new ExecutionEngine(_mockUow.Object, connectorLifecycle, emergencyClose, positionCloser, _mockUserSettings.Object, Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue), mockBalanceAggregator.Object, Mock.Of<IPreflightSlippageGuard>(), NullLogger<ExecutionEngine>.Instance);
    }

    private static OrderResultDto SuccessOrder(string orderId = "1", decimal price = 3000m, decimal qty = 0.1m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = qty };

    private static OrderResultDto FailOrder(string error = "Exchange down") =>
        new() { Success = false, Error = error };

    // ── ExecutionEngine: Zero Entry Price Guard ──────────────────────

    [Fact]
    public async Task OpenPosition_BothEntryPricesNonZero_TransitionsToOpen()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockPositions.Verify(p => p.Update(It.Is<ArbitragePosition>(
            pos => pos.Status == PositionStatus.Open)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task OpenPosition_ZeroLongPrice_MarkPriceFallbackSucceeds_TransitionsToOpen()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "long-1", FilledPrice = 0, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));
        // Mark price fallback
        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);

        var result = await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task OpenPosition_ZeroEntryPrices_FallbackFails_EmergencyClosed()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "long-1", FilledPrice = 0, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 0, FilledQuantity = 0.1m });
        // Mark price: first call (pre-flight) returns 3000, second call (fallback) returns 0
        _mockLongConnector
            .SetupSequence(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m) // pre-flight
            .ReturnsAsync(0m);   // fallback
        _mockShortConnector
            .SetupSequence(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m) // pre-flight
            .ReturnsAsync(0m);   // fallback

        var result = await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Zero entry prices");
        _mockPositions.Verify(p => p.Update(It.Is<ArbitragePosition>(
            pos => pos.Status == PositionStatus.EmergencyClosed)), Times.AtLeastOnce);
    }

    // ── ExecutionEngine: One-Sided Close Retry with Leg Flags ───────

    [Fact]
    public async Task ClosePosition_LongLegAlreadyClosed_OnlyDispatchesShort()
    {
        var position = MakeClosingPosition();
        position.LongLegClosed = true;

        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3001m, FilledQuantity = 0.1m });

        await _engine.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed);

        // Long leg should NOT be called
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()), Times.Never);
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClosePosition_ShortLegAlreadyClosed_OnlyDispatchesLong()
    {
        var position = MakeClosingPosition();
        position.ShortLegClosed = true;

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3000m, FilledQuantity = 0.1m });

        await _engine.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed);

        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()), Times.Once);
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClosePosition_BothLegsAlreadyClosed_Finalizes()
    {
        var position = MakeClosingPosition();
        position.LongLegClosed = true;
        position.ShortLegClosed = true;

        await _engine.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed);

        position.Status.Should().Be(PositionStatus.Closed);
        position.ClosedAt.Should().NotBeNull();
        // Neither leg should be dispatched
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClosePosition_PartialFailure_SetsLegFlagOnSuccessfulSide()
    {
        var position = MakeClosingPosition();

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3000m, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Exchange error"));

        await _engine.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed);

        position.LongLegClosed.Should().BeTrue("long leg succeeded");
        position.ShortLegClosed.Should().BeFalse("short leg failed");
        position.Status.Should().NotBe(PositionStatus.Closed, "position should stay in Closing for retry");
    }

    private ArbitragePosition MakeClosingPosition()
    {
        return new ArbitragePosition
        {
            Id = 42,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0003m,
            AccumulatedFunding = 0.5m,
            EntryFeesUsdc = 0.1m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };
    }

    // ── PositionHealthMonitor: Zero-Price Escalation ────────────────

    [Fact]
    public async Task HealthMonitor_FirstZeroPriceCheck_DoesNotClose()
    {
        var monitor = CreateHealthMonitor();
        var position = MakeZeroPricePosition();
        SetupHealthMonitorDefaults([position]);

        var result = await monitor.CheckAndActAsync();

        result.ToClose.Should().BeEmpty("first zero-price check should not trigger close");
    }

    [Fact]
    public async Task HealthMonitor_ThirdConsecutiveZeroPriceCheck_ForcesClose()
    {
        var monitor = CreateHealthMonitor();
        var position = MakeZeroPricePosition();
        SetupHealthMonitorDefaults([position]);

        // Run 3 consecutive checks
        await monitor.CheckAndActAsync();
        await monitor.CheckAndActAsync();
        var result = await monitor.CheckAndActAsync();

        result.ToClose.Should().ContainSingle("third consecutive check should trigger force-close");
        result.ToClose[0].Position.Id.Should().Be(position.Id);
        result.ToClose[0].Reason.Should().Be(CloseReason.StopLoss);
    }

    [Fact]
    public async Task HealthMonitor_ZeroPriceCountResetsWhenPricesValid()
    {
        // Set up a market data cache that returns valid prices (used for the 3rd check)
        var mockMarketDataCache = new Mock<IMarketDataCache>();
        mockMarketDataCache
            .Setup(c => c.GetMarkPrice(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(3000m);

        // Use the SAME monitor instance throughout so the counter state is preserved
        var monitor = CreateHealthMonitor(mockMarketDataCache.Object);
        var zeroPricePosition = MakeZeroPricePosition();
        SetupHealthMonitorDefaults([zeroPricePosition]);

        // Two zero-price checks — counter increments to 2
        await monitor.CheckAndActAsync();
        await monitor.CheckAndActAsync();

        // Now give the position valid prices — 3rd check should see valid prices and reset counter
        zeroPricePosition.LongEntryPrice = 3000m;
        zeroPricePosition.ShortEntryPrice = 3001m;

        var result = await monitor.CheckAndActAsync();
        result.ToClose.Should().BeEmpty("prices are now valid — counter should have reset to 0");

        // Set prices back to zero — 4th check should NOT close (counter reset to 0, now at 1)
        zeroPricePosition.LongEntryPrice = 0m;
        var result2 = await monitor.CheckAndActAsync();
        result2.ToClose.Should().BeEmpty("counter was reset; first check after re-zero should not close");
    }

    private readonly Mock<IUnitOfWork> _monitorUow = new();
    private readonly Mock<IBotConfigRepository> _monitorBotConfig = new();
    private readonly Mock<IPositionRepository> _monitorPositions = new();
    private readonly Mock<IAlertRepository> _monitorAlerts = new();
    private readonly Mock<IFundingRateRepository> _monitorFundingRates = new();
    private readonly Mock<IExchangeConnectorFactory> _monitorFactory = new();
    private readonly Mock<IExecutionEngine> _monitorExecutionEngine = new();

    private PositionHealthMonitor CreateHealthMonitor(IMarketDataCache? marketDataCache = null)
    {
        _monitorUow.Setup(u => u.BotConfig).Returns(_monitorBotConfig.Object);
        _monitorUow.Setup(u => u.Positions).Returns(_monitorPositions.Object);
        _monitorUow.Setup(u => u.Alerts).Returns(_monitorAlerts.Object);
        _monitorUow.Setup(u => u.FundingRates).Returns(_monitorFundingRates.Object);
        _monitorUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _monitorBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);

        _monitorPositions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>()))
            .ReturnsAsync([]);

        var mockRefPrice = new Mock<IReferencePriceProvider>();
        mockRefPrice.Setup(r => r.GetUnifiedPrice(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0m);

        return new PositionHealthMonitor(
            _monitorUow.Object,
            _monitorFactory.Object,
            marketDataCache ?? new Mock<IMarketDataCache>().Object,
            mockRefPrice.Object,
            _monitorExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);
    }

    private void SetupHealthMonitorDefaults(List<ArbitragePosition> positions)
    {
        _monitorPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(positions);

        _monitorFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = -0.0001m },
                new() { ExchangeId = 2, AssetId = 1, RatePerHour = 0.0003m },
            });
    }

    private static ArbitragePosition MakeZeroPricePosition()
    {
        return new ArbitragePosition
        {
            Id = 99,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 100m,
            Leverage = 5,
            LongEntryPrice = 0m,       // zero!
            ShortEntryPrice = 3001m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0005m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };
    }

    // ── Review-v113: NB7 — Leverage cache ───────────────────────────

    [Fact]
    public async Task OpenPosition_SecondCall_ReusesLeverageCache()
    {
        // Arrange — GetMaxLeverageAsync returns 5 (matches configured leverage, no reduction)
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        // Act — call twice with the same opportunity
        await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);
        await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert — GetMaxLeverageAsync must be called exactly once per connector (cached on second call)
        _mockLongConnector.Verify(
            c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()),
            Times.Once,
            "second call within TTL must use cached leverage for Hyperliquid");
        _mockShortConnector.Verify(
            c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()),
            Times.Once,
            "second call within TTL must use cached leverage for Lighter");
    }

    [Fact]
    public async Task LeverageReduced_Alert_EmittedOnlyOnFirstCall()
    {
        // Arrange — exchange max leverage (3) is lower than configured (5), so leverage is reduced
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 3, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 3, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        // Act — call twice; the leverage warning should only fire once per connector
        await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);
        await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert — only 1 LeverageReduced alert total across both calls
        // Long connector reduces 5x→3x on call 1 (fires alert). Effective leverage is then 3.
        // Short connector max=3 is not > effective 3, so no short alert.
        // Call 2: cache hit, _leverageWarned already set → no duplicate alert.
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LeverageReduced)),
            Times.Once,
            "LeverageReduced alert must fire exactly once total (deduplicated by _leverageWarned across calls)");
    }

    // ── Review-v113: NB8 — Short-leg zero price fallback ────────────

    [Fact]
    public async Task OpenPosition_ZeroShortPrice_MarkPriceFallbackSucceeds_TransitionsToOpen()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3001m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 0, FilledQuantity = 0.1m });
        // Only short connector's mark price fallback should be called
        _mockShortConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3001m);

        var result = await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        // Short connector: called once for pre-flight + once for fallback = 2
        _mockShortConnector.Verify(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()), Times.Exactly(2));
        // Long connector: called once for pre-flight only (no fallback needed since its price was non-zero)
        _mockLongConnector.Verify(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Review-v113: NB2 — BothLegsAlreadyClosed PnL assertion ──────

    [Fact]
    public async Task ClosePosition_BothLegsAlreadyClosed_PnlIsApproximate()
    {
        var position = MakeClosingPosition();
        position.LongLegClosed = true;
        position.ShortLegClosed = true;
        position.AccumulatedFunding = 0.5m;
        position.EntryFeesUsdc = 0.1m;
        position.ExitFeesUsdc = 0.05m;

        await _engine.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed);

        position.Status.Should().Be(PositionStatus.Closed);
        position.RealizedPnl.Should().Be(0.5m - 0.1m - 0.05m, "PnL = funding - entry fees - exit fees");
        // NB2: Alert escalates approximate PnL as Critical
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Severity == AlertSeverity.Critical &&
                  al.Message.Contains("approximate"))), Times.AtLeastOnce);
    }

    // ── Review-v113: N3 — Alert.UserId assertions ───────────────────

    [Fact]
    public async Task OpenPosition_EmergencyClosed_AlertHasCorrectUserId()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "long-1", FilledPrice = 0, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 0, FilledQuantity = 0.1m });
        // Mark price: first call (pre-flight) returns 3000, second call (fallback) returns 0
        _mockLongConnector
            .SetupSequence(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m)
            .ReturnsAsync(0m);
        _mockShortConnector
            .SetupSequence(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m)
            .ReturnsAsync(0m);

        await _engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.UserId == TestUserId)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePosition_PartialFailure_AlertHasCorrectUserId()
    {
        var position = MakeClosingPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 3000m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Exchange error"));

        await _engine.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.UserId == position.UserId)), Times.AtLeastOnce);
    }
}
