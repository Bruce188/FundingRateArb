using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
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
    private readonly ExecutionEngine _engine;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        DefaultLeverage = 5,
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
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        _mockLongConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockShortConnector.Setup(c => c.ExchangeName).Returns("Lighter");

        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);

        _engine = new ExecutionEngine(_mockUow.Object, _mockFactory.Object, _mockUserSettings.Object, NullLogger<ExecutionEngine>.Instance);
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
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
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
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "long-1", FilledPrice = 0, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
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
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "long-1", FilledPrice = 0, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 0, FilledQuantity = 0.1m });
        // Mark price fallback also returns 0
        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        _mockShortConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);

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
        var monitor = CreateHealthMonitor();
        var zeroPricePosition = MakeZeroPricePosition();
        SetupHealthMonitorDefaults([zeroPricePosition]);

        // Two zero-price checks
        await monitor.CheckAndActAsync();
        await monitor.CheckAndActAsync();

        // Now give the position valid prices
        zeroPricePosition.LongEntryPrice = 3000m;
        zeroPricePosition.ShortEntryPrice = 3001m;

        // Set up mark prices for the health check
        var mockMarketDataCache = new Mock<IMarketDataCache>();
        mockMarketDataCache
            .Setup(c => c.GetMarkPrice(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(3000m);

        // Re-create monitor with a cache that returns prices
        var monitor2 = CreateHealthMonitor(mockMarketDataCache.Object);
        SetupHealthMonitorDefaults([zeroPricePosition]);

        var result = await monitor2.CheckAndActAsync();
        result.ToClose.Should().BeEmpty("prices are now valid — counter should have reset");

        // Set prices back to zero and check — should not close on first check
        zeroPricePosition.LongEntryPrice = 0m;
        var result2 = await monitor2.CheckAndActAsync();
        result2.ToClose.Should().BeEmpty("counter was reset, first check should not close");
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

        return new PositionHealthMonitor(
            _monitorUow.Object,
            _monitorFactory.Object,
            marketDataCache ?? new Mock<IMarketDataCache>().Object,
            _monitorExecutionEngine.Object,
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
}
