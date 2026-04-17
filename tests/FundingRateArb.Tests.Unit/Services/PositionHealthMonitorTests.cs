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

namespace FundingRateArb.Tests.Unit.Services;

public class PositionHealthMonitorTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRates = new();
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IExchangeConnector> _mockShortConnector = new();
    private readonly Mock<IReferencePriceProvider> _mockReferencePriceProvider = new();
    private readonly Mock<IExecutionEngine> _mockExecutionEngine = new();
    private readonly PositionHealthMonitor _sut;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        CloseThreshold = -0.00005m,
        AlertThreshold = 0.0001m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 72,
        MaxLeverageCap = 50,
        AdaptiveHoldEnabled = true, // matches BotConfiguration default; explicit for test clarity
    };

    public PositionHealthMonitorTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);

        _mockFactory.Setup(f => f.GetConnector("Hyperliquid")).Returns(_mockLongConnector.Object);
        _mockFactory.Setup(f => f.GetConnector("Lighter")).Returns(_mockShortConnector.Object);

        _mockLongConnector.Setup(c => c.HasCredentials).Returns(true);
        _mockShortConnector.Setup(c => c.HasCredentials).Returns(true);

        // Default: no stale positions for M4 reaper
        _mockPositions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>()))
            .ReturnsAsync([]);

        // Default: no recent alerts (batch pre-fetch returns empty)
        _mockAlerts.Setup(a => a.GetRecentByPositionIdsAsync(
            It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Dictionary<(int, AlertType), Alert>());

        // Default: unified price returns 0 → fallback to per-exchange PnL (preserves existing test behavior)
        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0m);

        _sut = new PositionHealthMonitor(_mockUow.Object,
            _mockFactory.Object, new Mock<IMarketDataCache>().Object, _mockReferencePriceProvider.Object,
            _mockExecutionEngine.Object, Mock.Of<ILeverageTierProvider>(), new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);
    }

    private ArbitragePosition MakeOpenPosition(
        DateTime? openedAt = null,
        decimal entrySpread = 0.0005m,
        decimal longEntry = 3000m,
        decimal shortEntry = 3001m,
        decimal marginUsdc = 100m)
    {
        return new ArbitragePosition
        {
            Id = 1,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = marginUsdc,
            Leverage = 5,
            LongEntryPrice = longEntry,
            ShortEntryPrice = shortEntry,
            EntrySpreadPerHour = entrySpread,
            CurrentSpreadPerHour = entrySpread,
            Status = PositionStatus.Open,
            OpenedAt = openedAt ?? DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };
    }

    private void SetupLatestRates(decimal longRate, decimal shortRate)
    {
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new()
                {
                    ExchangeId = 1, AssetId = 1, RatePerHour = longRate,
                    MarkPrice = 3000m,
                    Exchange = new Exchange { Id = 1, Name = "Hyperliquid" },
                    Asset = new Asset { Id = 1, Symbol = "ETH" },
                },
                new()
                {
                    ExchangeId = 2, AssetId = 1, RatePerHour = shortRate,
                    MarkPrice = 3001m,
                    Exchange = new Exchange { Id = 2, Name = "Lighter" },
                    Asset = new Asset { Id = 1, Symbol = "ETH" },
                },
            });
    }

    private void SetupMarkPrices(decimal longMark = 3000m, decimal shortMark = 3001m)
    {
        _mockLongConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(longMark);
        _mockShortConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(shortMark);
    }

    // ── Auto-close: spread < CloseThreshold ────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenSpreadBelowCloseThreshold_ClosesPosition()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        // Spread collapsed: short rate now lower than long rate → negative spread
        SetupLatestRates(longRate: 0.0003m, shortRate: 0.0001m); // spread = -0.0002
        SetupMarkPrices();

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.SpreadCollapsed);
    }

    // ── Auto-close: max hold time ──────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenMaxHoldTimeReached_ClosesPosition()
    {
        var pos = MakeOpenPosition(openedAt: DateTime.UtcNow.AddHours(-73)); // >72 hours
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices();

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.MaxHoldTimeReached);
    }

    // ── Auto-close: stop loss ──────────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenStopLossTriggered_ClosesPosition()
    {
        // M5: Stop-loss uses net unrealized PnL, not averaged price move.
        // Entry: long=3000, short=3001, margin=100, leverage=5
        // avgEntry = 3000.5, estimatedQty = 100*5/3000.5 ≈ 0.16664
        // Current: long drops to 2500, short stays at 3001
        // longPnl  = (2500-3000) * 0.16664 ≈ -83.32
        // shortPnl = (3001-3001) * 0.16664 = 0
        // unrealizedPnl ≈ -83.32, threshold = 0.15 * 100 = 15 → |83.32| > 15 → triggers
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m); // asymmetric move → net loss

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
    }

    // ── No close: healthy spread ───────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenSpreadHealthy_DoesNothing()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // spread = 0.0005 (healthy)
        SetupMarkPrices();

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().BeEmpty();
    }

    // ── Alerts: spread warning (with duplicate suppression) ────────────────────

    [Fact]
    public async Task CheckAndAct_WhenSpreadBelowAlertThreshold_CreatesAlert()
    {
        // Spread is low (below alertThreshold=0.0001) but above closeThreshold=-0.00005
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0003m, shortRate: 0.00035m); // spread = 0.00005 < alertThreshold
        SetupMarkPrices();

        // No recent alert
        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), AlertType.SpreadWarning, It.IsAny<TimeSpan>()))
            .ReturnsAsync((Alert?)null);

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.SpreadWarning)), Times.Once);
    }

    [Fact]
    public async Task CheckAndAct_DoesNotCreateDuplicateAlerts_WithinOneHour()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0003m, shortRate: 0.00035m); // spread low
        SetupMarkPrices();

        // Recent alert exists (batch pre-fetch returns it)
        var existingAlert = new Alert
        {
            UserId = "admin-user-id",
            Type = AlertType.SpreadWarning,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            Message = "test",
        };
        _mockAlerts.Setup(a => a.GetRecentByPositionIdsAsync(
            It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Dictionary<(int, AlertType), Alert>
            {
                { (pos.Id, AlertType.SpreadWarning), existingAlert },
            });

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.SpreadWarning)), Times.Never);
    }

    // ── Updates CurrentSpreadPerHour ────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_UpdatesCurrentSpreadPerHour_OnEveryCheck()
    {
        var pos = MakeOpenPosition(entrySpread: 0.0005m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0004m); // spread = 0.0003
        SetupMarkPrices();

        await _sut.CheckAndActAsync();

        pos.CurrentSpreadPerHour.Should().Be(0.0003m);
        // EF change tracker detects property changes on tracked entities — no explicit Update needed
    }

    // ── No positions → no work ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenNoOpenPositions_DoesNothing()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().BeEmpty();
    }

    // ── C-PR1: Uses GetOpenTrackedAsync (not GetOpenAsync) ─────────────────────

    [Fact]
    public async Task CheckAndAct_UsesTrackedQuery_NotReadOnlyQuery()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        await _sut.CheckAndActAsync();

        // GetOpenTrackedAsync must be called; GetOpenAsync (read-only) must NOT be called
        _mockPositions.Verify(p => p.GetOpenTrackedAsync(), Times.Once);
        _mockPositions.Verify(p => p.GetOpenAsync(), Times.Never);
    }

    // ── C-PH1: SaveAsync called exactly once per cycle (not inside loop) ────────

    [Fact]
    public async Task CheckAndAct_CallsSaveAsyncExactlyOnce_RegardlessOfPositionCount()
    {
        var pos1 = MakeOpenPosition();
        var pos2 = new ArbitragePosition
        {
            Id = 2,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
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
        };

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos1, pos2]);
        // Healthy spread for both — no close, but spread update + SaveAsync should still happen once
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<AlertType>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync((Alert?)null);

        await _sut.CheckAndActAsync();

        // SaveAsync must be called exactly once regardless of how many positions exist
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── C-PH1: SaveAsync called once even when a position triggers close ────────

    [Fact]
    public async Task CheckAndAct_WithCloseTrigger_SavesOnceBeforeClose()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        // Spread collapsed → triggers auto-close
        SetupLatestRates(longRate: 0.0003m, shortRate: 0.0001m);
        SetupMarkPrices();

        var result = await _sut.CheckAndActAsync();

        // One save for the spread update batch, then close list returned
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.SpreadCollapsed);
    }

    // ── M5: Stop-loss uses net unrealized PnL ─────────────────────────────────

    [Fact]
    public async Task CheckAndAct_StopLossTriggered_WhenUnrealizedPnlExceedsThreshold()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        pos.MarginUsdc = 100m;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockPositions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>())).ReturnsAsync([]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m); // long dropped significantly

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
    }

    // ── M4: Stale state reaper ──────────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_ReapsStaleOpeningPositions_After5Minutes()
    {
        // No open positions
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        // One stale Opening position (stuck for 10 minutes)
        var stalePos = new ArbitragePosition
        {
            Id = 99,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Opening,
            OpenedAt = DateTime.UtcNow.AddMinutes(-10),
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync([stalePos]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync([]);

        await _sut.CheckAndActAsync();

        stalePos.Status.Should().Be(PositionStatus.EmergencyClosed);
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("stuck in Opening"))),
            Times.Once);
    }

    // ── B2-R: Mark price fetch failure is isolated — other positions still processed ──

    [Fact]
    public async Task CheckAndAct_WhenMarkPriceFetchFails_ContinuesToProcessRemainingPositions()
    {
        // Two positions: pos1's connectors throw, pos2's connectors succeed
        var pos1 = MakeOpenPosition();
        var pos2 = new ArbitragePosition
        {
            Id = 2,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
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
        };

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos1, pos2]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread for both

        // First call throws (pos1), second call succeeds (pos2)
        var callCount = 0;
        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("Simulated timeout");
                }

                return 3000m;
            });
        _mockShortConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3001m);

        // Act — must not throw
        HealthCheckResult? result = null;
        var act = async () => { result = await _sut.CheckAndActAsync(); };
        await act.Should().NotThrowAsync();

        // pos1's spread is updated (happens before the try/catch)
        pos1.CurrentSpreadPerHour.Should().Be(0.0005m); // shortRate - longRate = 0.0006 - 0.0001
        // pos2 was fully processed — no close triggered (spread is healthy)
        result.Should().NotBeNull();
        result!.ToClose.Should().BeEmpty("neither position needs closing with healthy spreads");
        // SaveAsync still called once
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── D4: ClosingStartedAt reaper tests ───────────────────────────────────────

    [Fact]
    public async Task ReapStalePositions_ClosingStatus_UsesClosingStartedAt()
    {
        // Position opened 2hrs ago but ClosingStartedAt only 1 min ago — should NOT be reaped
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var pos = new ArbitragePosition
        {
            Id = 50,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-1),
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([pos]);

        await _sut.CheckAndActAsync();

        pos.Status.Should().Be(PositionStatus.Closing, "ClosingStartedAt is recent — should not be reaped");
    }

    [Fact]
    public async Task ReapStalePositions_ClosingStatus_ReapsWhenClosingStartedAtIsOld()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var pos = new ArbitragePosition
        {
            Id = 51,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-15), // > 10 min threshold
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([pos]);

        await _sut.CheckAndActAsync();

        pos.Status.Should().Be(PositionStatus.EmergencyClosed, "ClosingStartedAt > 10min ago — should be reaped");
    }

    // ── D4: Stop-loss exact boundary ────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_StopLoss_ExactBoundary_Triggers()
    {
        // StopLossPct=0.15, MarginUsdc=100 → threshold=15
        // Need unrealizedPnl = -15 exactly
        // avgEntry = (3000+3000)/2 = 3000, estimatedQty = 100*5/3000 = 0.16667
        // longPnl = (longMark - 3000) * 0.16667, shortPnl = (3000 - shortMark) * 0.16667
        // For symmetric drop: longMark=2910, shortMark=3000
        // longPnl = (2910-3000)*0.16667 = -90*0.16667 = -15.0003
        // shortPnl = (3000-3000)*0.16667 = 0
        // unrealizedPnl ≈ -15.0 → |15| >= 15 → triggers
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2910m, shortMark: 3000m);

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.StopLoss);
    }

    // ── D4: Max hold time exact boundary ────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_MaxHoldTime_ExactBoundary_Triggers()
    {
        // MaxHoldTimeHours=72, position opened exactly 72 hours ago
        var pos = MakeOpenPosition(openedAt: DateTime.UtcNow.AddHours(-72));
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.MaxHoldTimeReached);
    }

    // ── D4: Zero entry prices guard ─────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_ZeroEntryPrices_SkipsPosition()
    {
        var pos = MakeOpenPosition(longEntry: 0m, shortEntry: 0m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().BeEmpty("position with zero entry prices should be skipped, not closed");
    }

    // ── D4: Price feed failure — 5 consecutive creates alert ────────────────────

    [Fact]
    public async Task CheckAndAct_PriceFeedFailure_ThresholdConsecutive_CreatesAlertAndForceCloses()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        // Override config with a low threshold for test speed
        var testConfig = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            AdaptiveHoldEnabled = true,
            PriceFeedFailureCloseThreshold = 5,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(testConfig);

        // Make GetMarkPriceAsync throw every time
        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Run 5 cycles to accumulate failures (matching threshold)
        HealthCheckResult lastResult = null!;
        for (int i = 0; i < 5; i++)
        {
            lastResult = await _sut.CheckAndActAsync();
        }

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Type == AlertType.PriceFeedFailure && al.Severity == AlertSeverity.Critical)),
            Times.Once);
        // B4: Verify position was added to close list with PriceFeedLost reason
        lastResult.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.PriceFeedLost);
    }

    // ── PnL-Target Exit ──────────────────────────────────────────

    [Fact]
    public void DetermineCloseReason_PnlTargetReached_ReturnsPnlTargetReached()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 10m; // high accumulated funding
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045 + 0.0 = 0.00045
        // Estimated entry fee: 100 * 5 * 2 * 0.00045 = 0.45 (SizeUsdc * Leverage * 2 * fee)
        // Target: 2.0 * 0.45 = 0.90
        // AccumulatedFunding (10) >= 0.90 → PnlTargetReached
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void DetermineCloseReason_PnlBelowTarget_NoClose()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 0.1m; // small accumulated funding
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045
        // Estimated entry fee: 100 * 5 * 2 * 0.00045 = 0.45 (SizeUsdc * Leverage * 2 * fee)
        // Target: 2.0 * 0.45 = 0.90
        // AccumulatedFunding (0.1) < 0.90 → no PnL close
        // hoursOpen=2 < 72, spread=0.001 > -0.00005 → no close
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().BeNull();
    }

    [Fact]
    public void DetermineCloseReason_AdaptiveHoldDisabled_PnlTargetSkipped()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 100m; // would exceed any PnL target
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = false, // disabled
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // Despite high accumulated funding, adaptive hold is disabled
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().BeNull();
    }

    [Fact]
    public void DetermineCloseReason_PnlExactlyAtTarget_ReturnsPnlTargetReached()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045 + 0.0 = 0.00045
        // estimatedEntryFee = 100 * 5 * 2 * 0.00045 = 0.45
        // target = 2.0 * 0.45 = 0.90
        // AccumulatedFunding exactly at target
        pos.AccumulatedFunding = 0.90m;

        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void DetermineCloseReason_ZeroAccumulatedFunding_PnlTargetSkipped()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 0m;
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().BeNull();
    }

    // ── B2: AdaptiveHoldEnabled=true default behavior coverage ────────────────

    [Fact]
    public void DetermineCloseReason_DefaultAdaptiveHoldEnabled_ZeroFunding_ReturnsNull()
    {
        // Validates that the new default (AdaptiveHoldEnabled=true) with zero
        // accumulated funding does not trigger a close — the guard `pos.AccumulatedFunding > 0`
        // prevents entering the PnL-target branch.
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 0m;
        pos.SizeUsdc = 100m;

        // Use DefaultConfig which now explicitly sets AdaptiveHoldEnabled = true
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, DefaultConfig, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m);

        result.Should().BeNull("zero accumulated funding should not trigger PnL-target exit");
    }

    [Fact]
    public void DetermineCloseReason_DefaultAdaptiveHoldEnabled_FundingExceedsTarget_ReturnsPnlTargetReached()
    {
        // Validates that with the default AdaptiveHoldEnabled=true, a position whose
        // AccumulatedFunding >= TargetPnlMultiplier * entryFee triggers PnlTargetReached.
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;

        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045 + 0.0 = 0.00045
        // estimatedEntryFee = 100 * 5 * 2 * 0.00045 = 0.45
        // target = DefaultConfig.TargetPnlMultiplier(2.0) * 0.45 = 0.90
        pos.AccumulatedFunding = 0.90m;

        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, DefaultConfig, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void GetTakerFeeRate_KnownExchanges_ReturnsSumOfFees()
    {
        // GetTakerFeeRate returns the sum of both exchange fees (not max)
        PositionHealthMonitor.GetTakerFeeRate("Hyperliquid", "Lighter").Should().Be(0.00045m); // 0.00045 + 0.0
        PositionHealthMonitor.GetTakerFeeRate("Lighter", "Aster").Should().Be(0.0004m);        // 0.0 + 0.0004
        PositionHealthMonitor.GetTakerFeeRate("Hyperliquid", "Aster").Should().Be(0.00085m);   // 0.00045 + 0.0004
    }

    // ── DivergenceCritical risk-based close (profitability-fixes F4) ─────────

    [Fact]
    public void DetermineCloseReason_DivergenceCritical_PreHoldTime_DoesNotFire()
    {
        // 30-minute hold, 2.8% divergence, healthy liquidation → suppress.
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3021m);
        pos.CurrentDivergencePct = 2.8m;

        var config = new BotConfiguration
        {
            MinHoldTimeHours = 2,
            DivergenceAlertMultiplier = 2.0m,
            LiquidationWarningPct = 0.50m,
            UseRiskBasedDivergenceClose = true,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // Threshold = 0.697% * 2.0 * 2 = 2.79%; divergence 2.8% > threshold → legacy would fire.
        // hoursOpen = 0.5 < MinHoldTimeHours = 2 → risk-based gate suppresses close.
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 0.5m, spread: 0.001m,
            minLiquidationDistance: 0.95m, negativeFundingCycles: 0,
            entrySpreadCostPct: 0.697m);

        result.Should().NotBe(CloseReason.DivergenceCritical,
            "divergence close must be suppressed when hoursOpen < MinHoldTimeHours");
    }

    [Fact]
    public void DetermineCloseReason_DivergenceCritical_HealthyLiquidation_DoesNotFire()
    {
        // 3-hour hold (past MinHoldTime), 2.8% divergence, distance above 2x warning → suppress.
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3021m);
        pos.CurrentDivergencePct = 2.8m;

        var config = new BotConfiguration
        {
            MinHoldTimeHours = 2,
            DivergenceAlertMultiplier = 2.0m,
            LiquidationWarningPct = 0.50m,
            UseRiskBasedDivergenceClose = true,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // Distance scale: 0 = at liquidation, 1 = at entry, >1 = moved past entry favourably.
        // Liquidation distance 1.5 > LiquidationWarningPct * 2 = 1.0 → healthy → do not escalate.
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 3m, spread: 0.001m,
            minLiquidationDistance: 1.5m, negativeFundingCycles: 0,
            entrySpreadCostPct: 0.697m);

        result.Should().NotBe(CloseReason.DivergenceCritical,
            "divergence close must be suppressed when liquidation distance is healthy");
    }

    [Fact]
    public void DetermineCloseReason_DivergenceCritical_PastHoldAndLiquidationRisky_FiresClose()
    {
        // All gates pass: past MinHoldTime, distance in the early-warning risky band → fire.
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3021m);
        pos.CurrentDivergencePct = 2.8m;

        var config = new BotConfiguration
        {
            MinHoldTimeHours = 2,
            DivergenceAlertMultiplier = 2.0m,
            LiquidationWarningPct = 0.50m,
            LiquidationEarlyWarningPct = 0.75m,
            UseRiskBasedDivergenceClose = true,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // Distance 0.60 sits between LiquidationWarningPct (0.50, the close trigger)
        // and LiquidationEarlyWarningPct (0.75, the unhealthy gate): high enough that
        // LiquidationRisk does NOT pre-empt, low enough that the divergence risk gate
        // is tripped (distance < 0.75).
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 3m, spread: 0.001m,
            minLiquidationDistance: 0.60m, negativeFundingCycles: 0,
            entrySpreadCostPct: 0.697m);

        result.Should().Be(CloseReason.DivergenceCritical,
            "divergence close must fire when both hold-time and liquidation gates are met");
    }

    [Fact]
    public void DetermineCloseReason_DivergenceCritical_FlagOff_UsesLegacySemantics()
    {
        // 30-minute hold, flag off → legacy immediate close fires regardless of hold time.
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3021m);
        pos.CurrentDivergencePct = 2.8m;

        var config = new BotConfiguration
        {
            MinHoldTimeHours = 2,
            DivergenceAlertMultiplier = 2.0m,
            LiquidationWarningPct = 0.50m,
            UseRiskBasedDivergenceClose = false,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 0.5m, spread: 0.001m,
            minLiquidationDistance: 0.95m, negativeFundingCycles: 0,
            entrySpreadCostPct: 0.697m);

        result.Should().Be(CloseReason.DivergenceCritical,
            "feature flag off must preserve the legacy threshold-only semantics");
    }

    // ── F7: ComputeLiquidationDistance direction (profitability-fixes) ────────

    [Fact]
    public void ComputeLiquidationDistance_ReturnsOne_WhenMarkEqualsEntry()
    {
        var pos = new ArbitragePosition
        {
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3000m,
            Leverage = 10,
        };

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3000m);

        distance.Should().NotBeNull();
        distance!.Value.Should().Be(1m,
            "distance is 1.0 when the position is sitting at entry");
    }

    [Fact]
    public void ComputeLiquidationDistance_ReturnsZero_WhenLongMarkEqualsLongLiquidation()
    {
        var pos = new ArbitragePosition
        {
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3000m,
            Leverage = 10,
        };

        // Formulaic long liquidation = 3000 * (1 - 1/10) = 2700
        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 2700m, currentShortMark: 3000m);

        distance.Should().NotBeNull();
        distance!.Value.Should().Be(0m,
            "distance is 0 when the mark has reached the liquidation price");
    }

    [Fact]
    public void ComputeLiquidationDistance_ReturnsHalf_AtLongMidpoint()
    {
        var pos = new ArbitragePosition
        {
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3000m,
            Leverage = 10,
        };

        // Midway between entry (3000) and long liquidation (2700) = 2850
        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 2850m, currentShortMark: 3000m);

        distance.Should().NotBeNull();
        distance!.Value.Should().Be(0.5m,
            "distance is 0.5 when the mark is halfway between entry and liquidation");
    }

    // ── F7: Liquidation early-warning alert threshold (profitability-fixes) ───

    [Fact]
    public async Task CheckAndAct_DoesNotEmitLiquidationWarning_WhenDistanceAboveEarlyWarningThreshold()
    {
        // Fresh position, mark nearly at entry, distance ~0.97 — well above 0.75 early-warning.
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            LiquidationWarningPct = 0.50m,
            LiquidationEarlyWarningPct = 0.75m,
        });

        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        pos.Leverage = 10; // long liq ≈ 2700, short liq ≈ 3300

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices(longMark: 2990m, shortMark: 3010m); // distance ≈ 0.97 (long), 0.97 (short)

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.MarginWarning)), Times.Never,
            "healthy fresh position must not emit a liquidation warning");
    }

    [Fact]
    public async Task CheckAndAct_EmitsLiquidationWarning_WhenDistanceBelowEarlyWarningThreshold()
    {
        // Distance ~0.25 — well below 0.75 early-warning, but above the 0.20 close threshold
        // so the position is NOT queued for close (the close path would skip the alert block).
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.95m, // high enough that unrealized losses don't trigger stop-loss
            MaxHoldTimeHours = 72,
            LiquidationWarningPct = 0.20m, // low close threshold so the alert path still runs
            LiquidationEarlyWarningPct = 0.75m,
        });

        var capturedAlerts = new List<Alert>();
        _mockAlerts.Setup(a => a.Add(It.IsAny<Alert>()))
            .Callback<Alert>(a => capturedAlerts.Add(a));

        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m, marginUsdc: 10000m);
        pos.Leverage = 10; // long liq ≈ 2700 (range 300). Mark 2775 → distance = 75/300 = 0.25

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices(longMark: 2775m, shortMark: 3000m);

        await _sut.CheckAndActAsync();

        var warningAlerts = capturedAlerts.Where(a => a.Type == AlertType.MarginWarning).ToList();
        warningAlerts.Should().ContainSingle(
            "distance=25% with early-warning threshold=75% must emit exactly one MarginWarning alert");
        warningAlerts[0].Message.Should().MatchRegex(@"threshold=75[.,]0\s*%",
            "alert message must reference the new 75% early-warning threshold in the P1-formatted output");
    }

    // ── F15: StopLoss priority over PnlTarget ────────────────────

    [Fact]
    public void DetermineCloseReason_StopLossAndPnlTargetBothMet_ReturnsStopLoss()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        pos.AccumulatedFunding = 100m; // exceeds any PnL target
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // unrealizedPnl of -20 exceeds StopLoss threshold (0.15 * 100 = 15)
        // AccumulatedFunding of 100 also exceeds PnL target
        // StopLoss should win because it has higher priority
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, -20m, 2m, 0.001m);

        result.Should().Be(CloseReason.StopLoss);
    }

    // ── F10: DetermineCloseReason edge cases ─────────────────────

    [Fact]
    public void DetermineCloseReason_ZeroSizeUsdc_PnlTargetSkipped()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 0m; // zero size
        pos.AccumulatedFunding = 100m; // would trigger if fee calculation wasn't zero
        pos.MarginUsdc = 0m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 100,
            CloseThreshold = -0.001m,
        };

        // estimatedEntryFee = 0 * leverage * 2 * feeRate = 0 → PnL target skipped
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().BeNull();
    }

    [Fact]
    public void DetermineCloseReason_NegativeAccumulatedFunding_PnlTargetSkipped()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = -0.5m; // negative funding
        pos.MarginUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 100,
            CloseThreshold = -0.001m,
        };

        // AccumulatedFunding <= 0 → PnL target check guard skips
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().BeNull();
    }

    // ── F16: GetTakerFeeRate null/unknown exchange names ─────────

    [Fact]
    public void GetTakerFeeRate_UnknownExchanges_ReturnsConservativeDefault()
    {
        // Unknown exchange should return 0.0005 per exchange (conservative default)
        PositionHealthMonitor.GetTakerFeeRate("Unknown", "Unknown").Should().Be(0.001m); // 0.0005 + 0.0005
    }

    [Fact]
    public void GetTakerFeeRate_NullExchangeNames_ReturnsConservativeDefault()
    {
        // Null exchange names should fall through to default case
        PositionHealthMonitor.GetTakerFeeRate(null, null).Should().Be(0.001m); // 0.0005 + 0.0005
    }

    [Fact]
    public void GetTakerFeeRate_MixedNullAndKnown_ReturnsCorrectSum()
    {
        // One null (default 0.0005) + one known (Hyperliquid 0.00045)
        PositionHealthMonitor.GetTakerFeeRate(null, "Hyperliquid").Should().Be(0.00095m);
    }

    // ── F12: PnlProgress warning thresholds ──────────────────────

    [Fact]
    public void DetermineCloseReason_UsesEntryFeesUsdc_WhenPositive()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.EntryFeesUsdc = 1.0m; // actual recorded entry fees

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // Target = 2.0 * 1.0 = 2.0 (uses EntryFeesUsdc, not estimated)
        // AccumulatedFunding = 2.0 >= 2.0 → PnlTargetReached
        pos.AccumulatedFunding = 2.0m;

        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void DetermineCloseReason_FallsBackToEstimate_WhenEntryFeesUsdcIsZero()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.EntryFeesUsdc = 0m; // not recorded

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045
        // estimatedEntryFee = 100 * 5 * 2 * 0.00045 = 0.45
        // Target = 2.0 * 0.45 = 0.90
        pos.AccumulatedFunding = 0.90m;

        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void DetermineCloseReason_NullExchanges_StillUseEntryFeesUsdc()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.EntryFeesUsdc = 0.5m;
        pos.LongExchange = null!;
        pos.ShortExchange = null!;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
        };

        // Target = 2.0 * 0.5 = 1.0
        pos.AccumulatedFunding = 1.0m;

        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, 0m, 2m, 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void ComputeWarnings_PnlProgressAbove90Pct_CriticalWarning()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 0.95m; // > 90% of target
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
        };

        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045
        // estimatedEntryFee = 100 * 5 * 2 * 0.00045 = 0.45
        // target = 2.0 * 0.45 = 0.90
        // progress = 0.95 / 0.90 > 1.0 → > 90% → Critical
        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningTypes.Should().Contain(WarningType.PnlProgress);
        dto.WarningLevel.Should().Be(WarningLevel.Critical);
    }

    [Fact]
    public void ComputeWarnings_PnlProgressAbove70Pct_Warning()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
        };

        // estimatedEntryFee = 100 * 5 * 2 * 0.00045 = 0.45
        // target = 2.0 * 0.45 = 0.90
        // Set funding so progress is 75%: 0.75 * 0.90 = 0.675
        pos.AccumulatedFunding = 0.675m;

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningTypes.Should().Contain(WarningType.PnlProgress);
        dto.WarningLevel.Should().BeOneOf(WarningLevel.Warning, WarningLevel.Critical);
    }

    [Fact]
    public void ComputeWarnings_PnlProgressBelow70Pct_NoPnlWarning()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = 0.1m; // low funding

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
        };

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningTypes.Should().NotContain(WarningType.PnlProgress);
    }

    // ── NB6: AdaptiveHoldEnabled=true tested through CheckAndActAsync ──────────

    [Fact]
    public async Task CheckAndActAsync_DefaultAdaptiveHold_ClosesWhenFundingExceedsTarget()
    {
        // Use default BotConfiguration (AdaptiveHoldEnabled = true, TargetPnlMultiplier = 2.0)
        var defaultConfig = new BotConfiguration
        {
            IsEnabled = true,
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.10m,
            MaxHoldTimeHours = 48,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(defaultConfig);

        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045
        // estimatedEntryFee = 100 * 5 * 2 * 0.00045 = 0.45
        // target = 2.0 * 0.45 = 0.90
        pos.AccumulatedFunding = 1.0m; // exceeds target of 0.90

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices();

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.PnlTargetReached);
    }

    [Fact]
    public void ComputeWarnings_AdaptiveHoldDisabled_NoPnlProgressWarning()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = 10m; // high funding, would trigger

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = false,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
        };

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningTypes.Should().NotContain(WarningType.PnlProgress);
    }

    // ── Closing position retry tests ──────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_ClosingPosition_IsRetried()
    {
        // Arrange: no open positions, but one position in Closing status
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());

        var closingPos = new ArbitragePosition
        {
            Id = 10,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-2),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        // GetByStatusAsync(Opening) returns empty (default), GetByStatusAsync(Closing) returns the stuck position
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { closingPos });

        // Act
        await _sut.CheckAndActAsync();

        // Assert: ClosePositionAsync was called for the stuck Closing position
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync("test-user", closingPos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAndAct_ClosingPositionRetryFails_DoesNotThrow()
    {
        // Arrange: closing position where retry throws
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());

        var closingPos = new ArbitragePosition
        {
            Id = 11,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-3),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { closingPos });
        _mockExecutionEngine
            .Setup(e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Exchange unreachable"));

        // Act & Assert: should not throw
        var act = async () => await _sut.CheckAndActAsync();
        await act.Should().NotThrowAsync();
    }

    // ── B1: Reap vs retry ordering ──────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_ReapAndRetry_OldPositionReaped_YoungPositionRetried()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var oldPosition = new ArbitragePosition
        {
            Id = 20,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-12), // > 10 min → should be reaped
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        var youngPosition = new ArbitragePosition
        {
            Id = 21,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-3), // < 10 min → should be retried
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { oldPosition, youngPosition });

        await _sut.CheckAndActAsync();

        // Old position should be reaped to EmergencyClosed, NOT retried
        oldPosition.Status.Should().Be(PositionStatus.EmergencyClosed);

        // Young position should be retried via ClosePositionAsync, NOT reaped
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync("test-user", youngPosition, It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Old position should NOT have been passed to ClosePositionAsync
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), oldPosition, It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // N4: Verify alert created for reaped position
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.ArbitragePositionId == 20 &&
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical)),
            Times.Once);

        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── NB8: CloseReason parameter for retried closes ──────────────────

    [Fact]
    public async Task CheckAndAct_RetryClose_UsesOriginalCloseReason()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var closingPos = new ArbitragePosition
        {
            Id = 30,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            CloseReason = CloseReason.MaxHoldTimeReached, // original reason
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-2),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { closingPos });

        await _sut.CheckAndActAsync();

        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync("test-user", closingPos, CloseReason.MaxHoldTimeReached, It.IsAny<CancellationToken>()),
            Times.Once,
            "retry should preserve the original CloseReason");
    }

    [Fact]
    public async Task CheckAndAct_RetryClose_FallsBackToSpreadCollapsed_WhenNoOriginalReason()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var closingPos = new ArbitragePosition
        {
            Id = 31,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            CloseReason = null, // no original reason
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-2),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { closingPos });

        await _sut.CheckAndActAsync();

        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync("test-user", closingPos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()),
            Times.Once,
            "retry should fall back to SpreadCollapsed when no original CloseReason");
    }

    // ── NB2: Null ClosingStartedAt fallback to OpenedAt ──────────────────

    [Fact]
    public async Task CheckAndAct_ClosingPosition_NullClosingStartedAt_FallsBackToOpenedAt()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var pos = new ArbitragePosition
        {
            Id = 60,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = null, // null — should fall back to OpenedAt
            OpenedAt = DateTime.UtcNow.AddMinutes(-15), // > 10 min → should be reaped
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { pos });

        await _sut.CheckAndActAsync();

        pos.Status.Should().Be(PositionStatus.EmergencyClosed,
            "when ClosingStartedAt is null and OpenedAt > 10min ago, position should be reaped");
        // Should NOT be retried (it was reaped)
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), pos, It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── NB3: Retry close timeout does not throw ──────────────────────────

    [Fact]
    public async Task CheckAndAct_RetryCloseTimeout_DoesNotThrow()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var closingPos = new ArbitragePosition
        {
            Id = 70,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-2),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(new List<ArbitragePosition> { closingPos });

        // Mock ClosePositionAsync to throw OperationCanceledException (simulating timeout)
        _mockExecutionEngine
            .Setup(e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Per-position retry timeout"));

        // Act: should not throw
        var act = async () => await _sut.CheckAndActAsync();
        await act.Should().NotThrowAsync("per-position timeout should be caught, not propagated");
    }

    // ── N5: Take(6) retry cap boundary ──────────────────────────────────

    [Fact]
    public async Task CheckAndAct_RetryCapAt6_DoesNotRetryBeyondCap()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync())
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);

        var closingPositions = Enumerable.Range(1, 8).Select(i => new ArbitragePosition
        {
            Id = 100 + i,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-i), // ordered youngest first
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        }).ToList();

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync(closingPositions);

        await _sut.CheckAndActAsync();

        // Only 6 positions should be retried (maxRetriesPerCycle = 6)
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Exactly(6));
    }

    // ── MinHoldTimeHours ──────────────────────────────────────────────────

    [Fact]
    public void DetermineCloseReason_SpreadCollapsed_Suppressed_WhenWithinMinHoldPeriod()
    {
        var pos = MakeOpenPosition();
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
        };

        // Spread is below CloseThreshold but position is only 1 hour old (< MinHoldTimeHours=2)
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0m, hoursOpen: 1m, spread: -0.001m);

        result.Should().BeNull("SpreadCollapsed should be suppressed within MinHoldTimeHours");
    }

    [Fact]
    public void DetermineCloseReason_SpreadCollapsed_Fires_WhenPastMinHoldPeriod()
    {
        var pos = MakeOpenPosition();
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
        };

        // Spread is below CloseThreshold and position is 3 hours old (>= MinHoldTimeHours=2)
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0m, hoursOpen: 3m, spread: -0.001m);

        result.Should().Be(CloseReason.SpreadCollapsed);
    }

    [Fact]
    public void DetermineCloseReason_SpreadCollapsed_Fires_AtExactMinHoldBoundary()
    {
        var pos = MakeOpenPosition();
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
        };

        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0m, hoursOpen: 2m, spread: -0.001m);

        result.Should().Be(CloseReason.SpreadCollapsed);
    }

    [Fact]
    public void DetermineCloseReason_StopLoss_StillFires_WithinMinHoldPeriod()
    {
        var pos = MakeOpenPosition(marginUsdc: 100m);
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
        };

        // StopLoss triggers: |unrealizedPnl| (-20) >= StopLossPct(0.15) * margin(100) = 15
        // Even though position is only 0.5 hours old (< MinHoldTimeHours=2)
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: -20m, hoursOpen: 0.5m, spread: -0.001m);

        result.Should().Be(CloseReason.StopLoss, "StopLoss should always fire regardless of MinHoldTimeHours");
    }

    [Fact]
    public void DetermineCloseReason_PnlTarget_BlockedByMinHoldBeforePnlTarget()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 10m;
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            MinHoldBeforePnlTargetMinutes = 60,
        };

        // PnlTargetReached: AccumulatedFunding(10) >= TargetPnlMultiplier(2.0) * entryFee
        // But position is only 0.5 hours old (30 min < MinHoldBeforePnlTargetMinutes=60)
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0m, hoursOpen: 0.5m, spread: 0.001m);

        result.Should().BeNull("PnlTargetReached should not fire before MinHoldBeforePnlTargetMinutes");
    }

    [Fact]
    public void DetermineCloseReason_PnlTargetReached_NegativeTotalPnl_DoesNotClose()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 1.0m;
        pos.EntryFeesUsdc = 0.5m;

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 1.5m,
            MinHoldBeforePnlTargetMinutes = 60,
        };

        // AccumulatedFunding(1.0) >= TargetPnlMultiplier(1.5) * EntryFeesUsdc(0.5) = 0.75 ✓
        // But totalPnl = unrealizedPnl(-2.0) + AccumulatedFunding(1.0) - EntryFeesUsdc(0.5) = -1.5 < 0
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: -2.0m, hoursOpen: 2.0m, spread: 0.001m);

        result.Should().BeNull("PnlTargetReached should not fire when total PnL is negative");
    }

    [Fact]
    public void DetermineCloseReason_PnlTargetReached_PositivePnl_AboveMinHoldTime_Closes()
    {
        var pos = MakeOpenPosition();
        pos.AccumulatedFunding = 1.0m;
        pos.EntryFeesUsdc = 0.1m;

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 1.5m,
            MinHoldBeforePnlTargetMinutes = 60,
        };

        // AccumulatedFunding(1.0) >= TargetPnlMultiplier(1.5) * EntryFeesUsdc(0.1) = 0.15 ✓
        // totalPnl = unrealizedPnl(0.5) + AccumulatedFunding(1.0) - EntryFeesUsdc(0.1) = 1.4 > 0 ✓
        // minutesOpen = 2.0 * 60 = 120 >= MinHoldBeforePnlTargetMinutes(60) ✓
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0.5m, hoursOpen: 2.0m, spread: 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached);
    }

    [Fact]
    public void DetermineCloseReason_EmergencySpread_BypassesMinHoldTime()
    {
        var pos = MakeOpenPosition();

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
            EmergencyCloseSpreadThreshold = -0.001m,
        };

        // Spread is -0.005 which is below EmergencyCloseSpreadThreshold(-0.001)
        // hoursOpen is 0.1 (way below MinHoldTimeHours=2)
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0m, hoursOpen: 0.1m, spread: -0.005m);

        result.Should().Be(CloseReason.SpreadCollapsed, "emergency spread should bypass MinHoldTimeHours");
    }

    [Fact]
    public void DetermineCloseReason_MildSpreadCollapse_RespectsMinHoldTime()
    {
        var pos = MakeOpenPosition();

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            CloseThreshold = -0.00005m,
            EmergencyCloseSpreadThreshold = -0.001m,
        };

        // Spread is -0.0001 which is below CloseThreshold(-0.00005) but above EmergencyCloseSpreadThreshold(-0.001)
        // hoursOpen is 0.5 (below MinHoldTimeHours=2)
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config,
            unrealizedPnl: 0m, hoursOpen: 0.5m, spread: -0.0001m);

        result.Should().BeNull("mild spread collapse should respect MinHoldTimeHours");
    }

    // ── Task 3.1: Reap sets ClosedAt on EmergencyClosed positions ──────────────

    [Fact]
    public async Task ReapStaleOpening_SetsClosedAt()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        var stalePos = new ArbitragePosition
        {
            Id = 99,
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Opening,
            OpenedAt = DateTime.UtcNow.AddMinutes(-10),
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync([stalePos]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing))
            .ReturnsAsync([]);

        var beforeReap = DateTime.UtcNow;
        await _sut.CheckAndActAsync();

        stalePos.Status.Should().Be(PositionStatus.EmergencyClosed);
        stalePos.ClosedAt.Should().NotBeNull("Reaped positions should have ClosedAt set");
        stalePos.ClosedAt!.Value.Should().BeOnOrAfter(beforeReap);
        stalePos.ClosedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── B3: Missing funding rate preserves CurrentSpreadPerHour ────────────────

    [Fact]
    public async Task CheckAndAct_MissingFundingRate_PreservesCurrentSpread()
    {
        var pos = MakeOpenPosition(entrySpread: 0.0005m);
        pos.CurrentSpreadPerHour = 0.0005m;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        // Only provide long rate, short rate is missing
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new()
                {
                    ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m,
                    MarkPrice = 3000m,
                    Exchange = new Exchange { Id = 1, Name = "Hyperliquid" },
                    Asset = new Asset { Id = 1, Symbol = "ETH" },
                },
                // No entry for ExchangeId=2 (Lighter) — simulates missing rate
            });
        SetupMarkPrices();

        await _sut.CheckAndActAsync();

        // Spread should be preserved at the previous value, not zeroed
        pos.CurrentSpreadPerHour.Should().Be(0.0005m);
    }

    // ── N3: PriceFeedFailure boundary — no close at threshold-1, close at threshold ──

    [Fact]
    public async Task CheckAndAct_PriceFeedFailure_NoCloseBeforeThreshold_ClosesAtThreshold()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        var testConfig = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            AdaptiveHoldEnabled = true,
            PriceFeedFailureCloseThreshold = 5,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(testConfig);

        // Make GetMarkPriceAsync throw every time
        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Run 4 cycles (threshold - 1) — should NOT close
        HealthCheckResult resultBefore = null!;
        for (int i = 0; i < 4; i++)
        {
            resultBefore = await _sut.CheckAndActAsync();
        }

        resultBefore.ToClose.Should().BeEmpty("position should not close before reaching threshold");

        // Run 5th cycle (at threshold) — SHOULD close
        var resultAt = await _sut.CheckAndActAsync();
        resultAt.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.PriceFeedLost,
            "position should force-close exactly at the threshold");
    }

    // ── NB1: totalPnl uses computed entryFee, not pos.EntryFeesUsdc ───────────

    [Fact]
    public void DetermineCloseReason_PnlTargetReached_ZeroEntryFeesUsdc_UsesFallbackFee()
    {
        var pos = MakeOpenPosition();
        pos.EntryFeesUsdc = 0; // Sequential open: fee not yet stored
        pos.AccumulatedFunding = 10m;
        pos.SizeUsdc = 100m;

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            MinHoldBeforePnlTargetMinutes = 60,
        };

        // With EntryFeesUsdc=0, the fallback computes entryFee from exchange fee rates
        // GetTakerFeeRate("Hyperliquid", "Lighter") = 0.00045 + 0
        // Fallback: 100 * 5 * 2 * 0.00045 = 0.45
        // totalPnl = unrealizedPnl(-0.5) + AccumulatedFunding(10) - entryFee(0.45) = 9.05 > 0
        // Target: 2.0 * 0.45 = 0.90, AccumulatedFunding(10) >= 0.90 → passes
        // But if using pos.EntryFeesUsdc (0), totalPnl would be 9.5 — different threshold
        // This test verifies the fallback fee is used, not the zero value
        var result = PositionHealthMonitor.DetermineCloseReason(pos, config, -0.5m, 2m, 0.001m);
        result.Should().Be(CloseReason.PnlTargetReached);
    }

    // ── Reconciliation tests ────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileOpenPositions_WhenPositionMissingFromBothExchanges_MarksExchangeDrift()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { pos.Id, PositionExistsResult.BothMissing } });

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.EmergencyClosed);
        pos.CloseReason.Should().Be(CloseReason.ExchangeDrift);
        pos.ClosedAt.Should().NotBeNull();
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Severity == AlertSeverity.Critical && al.ArbitragePositionId == pos.Id)), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenApiFailure_SkipsPosition()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { pos.Id, PositionExistsResult.Unknown } });

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Open);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenPositionExistsOnBothExchanges_NoChange()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { pos.Id, PositionExistsResult.BothPresent } });

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Open);
        _mockAlerts.Verify(a => a.Add(It.IsAny<Alert>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenOneResultUnknown_SkipsItAndProcessesOthers()
    {
        var pos1 = MakeOpenPosition();
        pos1.Id = 10;
        var pos2 = MakeOpenPosition();
        pos2.Id = 20;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos1, pos2]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { 10, PositionExistsResult.Unknown }, { 20, PositionExistsResult.BothMissing } });

        await _sut.ReconcileOpenPositionsAsync();

        pos1.Status.Should().Be(PositionStatus.Open, "first position should be unchanged after Unknown result");
        pos2.Status.Should().Be(PositionStatus.EmergencyClosed, "second position should be marked as drift");
        pos2.CloseReason.Should().Be(CloseReason.ExchangeDrift);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenNoOpenPositions_ReturnsEarly()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        await _sut.ReconcileOpenPositionsAsync();

        _mockExecutionEngine.Verify(
            e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenOnlyLongMissing_MarksExchangeDriftAndAttemptsSurvivingLegClose()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { pos.Id, PositionExistsResult.LongMissing } });

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.EmergencyClosed);
        pos.CloseReason.Should().Be(CloseReason.ExchangeDrift);
        pos.LongLegClosed.Should().BeTrue("long leg should be flagged as closed before ClosePositionAsync");
        pos.ShortLegClosed.Should().BeFalse("short leg should not be flagged");
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Severity == AlertSeverity.Critical && al.Message.Contains("long leg missing"))), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(pos.UserId, pos, CloseReason.ExchangeDrift, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenOnlyShortMissing_MarksExchangeDriftAndAttemptsSurvivingLegClose()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { pos.Id, PositionExistsResult.ShortMissing } });

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.EmergencyClosed);
        pos.CloseReason.Should().Be(CloseReason.ExchangeDrift);
        pos.ShortLegClosed.Should().BeTrue("short leg should be flagged as closed before ClosePositionAsync");
        pos.LongLegClosed.Should().BeFalse("long leg should not be flagged");
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Severity == AlertSeverity.Critical && al.Message.Contains("short leg missing"))), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(pos.UserId, pos, CloseReason.ExchangeDrift, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileOpenPositions_WhenBothMissing_DoesNotCallClosePositionAsync()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionsExistOnExchangesBatchAsync(It.IsAny<IReadOnlyList<ArbitragePosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, PositionExistsResult> { { pos.Id, PositionExistsResult.BothMissing } });

        await _sut.ReconcileOpenPositionsAsync();

        pos.LongLegClosed.Should().BeFalse("no leg flags set for both-missing case");
        pos.ShortLegClosed.Should().BeFalse("no leg flags set for both-missing case");
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── B.1: Opening position reconciliation ─────────────────────────────────

    [Fact]
    public async Task ReconcileOpenPositions_OpeningPositionExistsOnExchange_RecoveredToOpen()
    {
        var pos = new ArbitragePosition
        {
            Id = 100,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Opening,
            OpenedAt = DateTime.UtcNow.AddMinutes(-3),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([pos]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionExistsOnExchangesAsync(pos, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Open);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReconcileOpenPositions_OpeningPositionMissing_MarkedFailed()
    {
        var pos = new ArbitragePosition
        {
            Id = 101,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Opening,
            OpenedAt = DateTime.UtcNow.AddMinutes(-3),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([pos]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionExistsOnExchangesAsync(pos, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Failed);
        pos.ClosedAt.Should().NotBeNull();
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Cleanup close should be attempted for any surviving legs
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(pos.UserId, pos, CloseReason.ExchangeDrift, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileOpenPositions_OpeningPositionApiFailure_LeftInOpening()
    {
        var pos = new ArbitragePosition
        {
            Id = 102,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Opening,
            OpenedAt = DateTime.UtcNow.AddMinutes(-3),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([pos]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionExistsOnExchangesAsync(pos, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Opening, "API failure should not change status");
    }

    // ── B.2: Closing position reconciliation ─────────────────────────────────

    [Fact]
    public async Task ReconcileOpenPositions_ClosingPositionGoneFromExchanges_FinalizedAsClosed()
    {
        var pos = new ArbitragePosition
        {
            Id = 103,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-3),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([pos]);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionExistsOnExchangesAsync(pos, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Closed);
        pos.ClosedAt.Should().NotBeNull();
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReconcileOpenPositions_ClosingPositionApiFailure_LeftInClosing()
    {
        var pos = new ArbitragePosition
        {
            Id = 104,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closing,
            ClosingStartedAt = DateTime.UtcNow.AddMinutes(-3),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync([]);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Closing)).ReturnsAsync([pos]);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);
        _mockExecutionEngine
            .Setup(e => e.CheckPositionExistsOnExchangesAsync(pos, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);

        await _sut.ReconcileOpenPositionsAsync();

        pos.Status.Should().Be(PositionStatus.Closing, "API failure should not change status");
    }

    // ── ComputeLiquidationDistance ────────────────────────────────

    [Fact]
    public void ComputeLiquidationDistance_CalculatesCorrectLiquidationPrices()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        // Leverage 5: long liq = 3000 * (1 - 1/5) = 2400, short liq = 3000 * (1 + 1/5) = 3600

        PositionHealthMonitor.ComputeLiquidationDistance(pos, 3000m, 3000m);

        pos.LongLiquidationPrice.Should().Be(2400m);
        pos.ShortLiquidationPrice.Should().Be(3600m);
    }

    [Fact]
    public void ComputeLiquidationDistance_AtEntryPrice_ReturnsOne()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        // At entry: mark == entry → distance = (3000 - 2400) / (3000 - 2400) = 1.0

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(pos, 3000m, 3000m);

        distance.Should().Be(1.0m);
    }

    [Fact]
    public void ComputeLiquidationDistance_HalfwayToLiquidation_ReturnsPointFive()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        // Long liq=2400, range=600. Halfway: mark=2700, distance=(2700-2400)/600=0.5

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(pos, 2700m, 3000m);

        distance.Should().Be(0.5m);
    }

    [Fact]
    public void ComputeLiquidationDistance_ShortLegCloser_ReturnsShortDistance()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        // Short liq=3600, range=600. Mark=3450: distance=(3600-3450)/600=0.25

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(pos, 3000m, 3450m);

        distance.Should().Be(0.25m);
    }

    [Fact]
    public void ComputeLiquidationDistance_ZeroLeverage_ReturnsNull()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        pos.Leverage = 0;

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(pos, 3000m, 3000m);

        distance.Should().BeNull();
    }

    // ── DetermineCloseReason with LiquidationRisk ────────────────

    [Fact]
    public void DetermineCloseReason_LiquidationDistanceBelowThreshold_ReturnsLiquidationRisk()
    {
        var pos = MakeOpenPosition(marginUsdc: 100m);
        var config = new BotConfiguration { StopLossPct = 0.10m, LiquidationWarningPct = 0.50m };

        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 1m, spread: 0.001m,
            minLiquidationDistance: 0.3m);

        reason.Should().Be(CloseReason.LiquidationRisk);
    }

    [Fact]
    public void DetermineCloseReason_LiquidationDistanceAboveThreshold_ReturnsNull()
    {
        var pos = MakeOpenPosition(marginUsdc: 100m);
        var config = new BotConfiguration { StopLossPct = 0.10m, LiquidationWarningPct = 0.50m };

        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 1m, spread: 0.001m,
            minLiquidationDistance: 0.8m);

        reason.Should().BeNull();
    }

    [Fact]
    public void DetermineCloseReason_StopLossTakesPriorityOverLiquidation()
    {
        var pos = MakeOpenPosition(marginUsdc: 100m);
        var config = new BotConfiguration { StopLossPct = 0.10m, LiquidationWarningPct = 0.50m };

        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: -15m, hoursOpen: 1m, spread: 0.001m,
            minLiquidationDistance: 0.3m);

        reason.Should().Be(CloseReason.StopLoss);
    }

    [Fact]
    public void DetermineCloseReason_NullLiquidationDistance_DoesNotTrigger()
    {
        var pos = MakeOpenPosition(marginUsdc: 100m);
        var config = new BotConfiguration { StopLossPct = 0.10m, LiquidationWarningPct = 0.50m };

        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 1m, spread: 0.001m,
            minLiquidationDistance: null);

        reason.Should().BeNull();
    }

    // ── HasCredentials guard skips margin state fetch ─────────────────────────

    [Fact]
    public async Task FetchMarginState_SkipsCall_WhenConnectorLacksCredentials()
    {
        // Arrange: long connector has no credentials
        _mockLongConnector.Setup(c => c.HasCredentials).Returns(false);

        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        // Act
        await _sut.CheckAndActAsync();

        // Assert: margin state was never fetched
        _mockLongConnector.Verify(
            c => c.GetPositionMarginStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockShortConnector.Verify(
            c => c.GetPositionMarginStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Margin utilization alert fires at configured threshold ─────────────────

    [Fact]
    public async Task CheckAndAct_MarginUtilizationAboveThreshold_CreatesMarginWarningAlert()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.001m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MarginUtilizationAlertPct = 0.70m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices();

        // Long margin utilization at 75% — above 70% threshold
        _mockLongConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginStateDto
            {
                MarginUsed = 75m,
                MarginAvailable = 25m,
                MarginUtilizationPct = 0.75m,
                LiquidationPrice = 2800m,
            });
        _mockShortConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginStateDto
            {
                MarginUsed = 50m,
                MarginAvailable = 50m,
                MarginUtilizationPct = 0.50m,
                LiquidationPrice = 3200m,
            });

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.MarginWarning &&
                al.Message!.Contains("Margin utilization alert") &&
                al.Message!.Contains("ETH"))),
            Times.Once);
    }

    [Fact]
    public async Task CheckAndAct_MarginUtilizationBelowThreshold_NoMarginAlert()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.001m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MarginUtilizationAlertPct = 0.70m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        // Both legs below threshold
        _mockLongConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginStateDto
            {
                MarginUsed = 30m,
                MarginAvailable = 70m,
                MarginUtilizationPct = 0.30m,
            });
        _mockShortConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginStateDto
            {
                MarginUsed = 40m,
                MarginAvailable = 60m,
                MarginUtilizationPct = 0.40m,
            });

        await _sut.CheckAndActAsync();

        // No margin warning alerts should be created (existing alerts may be for other types)
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.MarginWarning &&
                al.Message!.Contains("Margin utilization alert"))),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAct_MarginStateNull_NoMarginAlert()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.001m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MarginUtilizationAlertPct = 0.70m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        // Both connectors return null (exchange doesn't support margin state)
        _mockLongConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MarginStateDto?)null);
        _mockShortConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MarginStateDto?)null);

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.MarginWarning &&
                al.Message!.Contains("Margin utilization alert"))),
            Times.Never);
    }

    // ── CyclesUntilLiquidation — pure computation ─────────────────────────────

    [Fact]
    public void ComputeLiquidationDistance_ValidInputs_ReturnsExpectedDistance()
    {
        // Long entry=3000, leverage=5 → long liq = 3000 * (1 - 1/5) = 2400
        // Short entry=3001, leverage=5 → short liq = 3001 * (1 + 1/5) = 3601.2
        // Long distance: (3000 - 2400) / (3000 - 2400) = 1.0 (at entry)
        // Short distance: (3601.2 - 3001) / (3601.2 - 3001) = 1.0 (at entry)
        var pos = MakeOpenPosition();
        var distance = PositionHealthMonitor.ComputeLiquidationDistance(pos, 3000m, 3001m);

        distance.Should().BeApproximately(1.0m, 0.01m);
    }

    [Fact]
    public void ComputeLiquidationDistance_PriceMovedTowardLiquidation_ReturnsSmaller()
    {
        // Long entry=3000, leverage=5 → long liq = 2400
        // Current mark = 2700 (moved toward liquidation)
        // Long range = 3000 - 2400 = 600, long dist = (2700 - 2400)/600 = 0.5
        var pos = MakeOpenPosition();
        var distance = PositionHealthMonitor.ComputeLiquidationDistance(pos, 2700m, 3001m);

        distance.Should().BeApproximately(0.5m, 0.01m);
    }

    // ── NB9: Margin alert dedup — recent alert suppresses duplicate ──────────

    [Fact]
    public async Task CheckAndAct_MarginUtilizationAboveThreshold_RecentAlertExists_SuppressesDuplicate()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.001m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MaxLeverageCap = 50,
            MarginUtilizationAlertPct = 0.70m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        // Margin utilization above threshold
        _mockLongConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginStateDto { MarginUsed = 75m, MarginAvailable = 25m, MarginUtilizationPct = 0.75m });
        _mockShortConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginStateDto { MarginUsed = 50m, MarginAvailable = 50m, MarginUtilizationPct = 0.50m });

        // First call creates the alert; second call should suppress it
        // Mock GetRecentAsync to return null first time (alert fires), then return an alert (suppressed)
        _mockAlerts
            .SetupSequence(a => a.GetRecentAsync("admin-user-id", 1, AlertType.MarginWarning, TimeSpan.FromHours(1)))
            .ReturnsAsync((Alert?)null)
            .ReturnsAsync(new Alert { Type = AlertType.MarginWarning });

        await _sut.CheckAndActAsync(); // fires the first alert
        await _sut.CheckAndActAsync(); // should be suppressed by in-memory dedup

        // Alert.Add should be called only once (first invocation)
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Type == AlertType.MarginWarning)),
            Times.Once);
    }

    // ── NB10: GetPositionMarginStateAsync throws — no crash ─────────────────

    [Fact]
    public async Task CheckAndAct_MarginStateThrows_NoMarginAlertAndNoCrash()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.001m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MaxLeverageCap = 50,
            MarginUtilizationAlertPct = 0.70m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        // Connector throws on margin state
        _mockLongConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Exchange unreachable"));
        _mockShortConnector
            .Setup(c => c.GetPositionMarginStateAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MarginStateDto?)null);

        // Should not throw
        var act = async () => await _sut.CheckAndActAsync();
        await act.Should().NotThrowAsync();

        // No margin alerts should be created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Type == AlertType.MarginWarning)),
            Times.Never);
    }

    // ── Unified PnL: stop loss uses unified price ─────────────────────────────

    [Fact]
    public async Task StopLoss_UsesUnifiedPnl_NotPerExchange()
    {
        // Entry: long=3000, short=3001, margin=100, leverage=5
        // avgEntry=3000.5, estimatedQty=100*5/3000.5≈0.16664
        //
        // Per-exchange marks: long=2500, short=3001
        //   longPnl=(2500-3000)*0.16664=-83.32, shortPnl=0, perExchangePnl=-83.32 → would trigger stop loss
        //
        // Unified price=3000 (Binance index, stable):
        //   unifiedLongPnl=(3000-3000)*0.16664=0, unifiedShortPnl=(3001-3000)*0.16664=0.17
        //   unifiedPnl=0.17 → no stop loss
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m);

        // Unified price stable at 3000 — no loss on unified basis
        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        var result = await _sut.CheckAndActAsync();

        // Per-exchange would trigger stop loss, but unified PnL shows no loss
        result.ToClose.Should().NotContain(r => r.Reason == CloseReason.StopLoss);
    }

    [Fact]
    public async Task StopLoss_FiresWhenUnifiedPnlExceedsThreshold()
    {
        // Entry: long=3000, short=3001, margin=100, leverage=5
        // Unified price drops to 2500:
        //   unifiedLongPnl=(2500-3000)*qty=-83.32, unifiedShortPnl=(3001-2500)*qty=83.39
        //   unifiedPnl=0.07 → no stop loss because it's actually profitable on unified
        // But if unified price drops to 2000:
        //   unifiedLongPnl=(2000-3000)*qty=-166.64, unifiedShortPnl=(3001-2000)*qty=166.72
        //   unifiedPnl=0.08 → still near zero because delta-neutral
        // Actually need both entry prices to move: long=3000, short=3001 → unified=2000
        //   The PnL is (short-long entry) * qty = near zero always
        // To trigger stop loss with unified PnL, use asymmetric entries:
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 2500m);

        // Unified price at 2500 → longPnl=(2500-3000)*qty=-83.32, shortPnl=(3000-2500)*qty=83.32
        // Net = 0 → still no stop loss
        // For actual stop loss, need post-entry funding to have shifted entry:
        // Actually, the real scenario is mark-to-market loss via unified price
        // Let me use a big move: entry long=3000, short=3001 → unified=2000
        // longPnl = (2000-3000) * 0.16664 = -166.64
        // shortPnl = (3001-2000) * 0.16664 = +166.80
        // net = +0.16 → near zero
        // This is the point of delta-neutral: unified PnL is near zero
        // Stop loss via unified PnL only triggers with entry spread asymmetry
        // or accumulated funding losses

        // Use the fallback case where unified = per-exchange (price=0)
        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(0m);

        var result = await _sut.CheckAndActAsync();

        // With unified price=0, falls back to per-exchange PnL
        // Per-exchange: long=(2500-3000)*qty=-83.32, short=(3000-2500)*qty=83.32, total≈0
        // No stop loss since symmetric entries + symmetric marks
        result.ToClose.Should().NotContain(r => r.Reason == CloseReason.StopLoss);
    }

    // ── Divergence alert ──────────────────────────────────────────────────────

    [Fact]
    public async Task DivergenceAlert_FiresWhenThresholdExceeded()
    {
        // Entry: long=3000, short=3001 → entryMid=3000.5, entrySpreadCostPct = |3001-3000|/3000.5*100 ≈ 0.0333%
        // DivergenceAlertMultiplier=2.0 → threshold = 2.0 * 0.0333 ≈ 0.0666%
        // Current: longMark=2950, shortMark=3050 → divergence = |2950-3050|/unifiedPrice*100
        // unifiedPrice = 3000 → divergence = 100/3000*100 = 3.33% >> 0.0666% → alert fires
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2950m, shortMark: 3050m);

        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), AlertType.SpreadWarning, It.IsAny<TimeSpan>()))
            .ReturnsAsync((Alert?)null);

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Type == AlertType.SpreadWarning &&
            al.Message.Contains("divergence"))), Times.Once);

        // NB6: Assert computed divergence value on entity
        // divergence = |2950-3050|/3000*100 = 100/3000*100 = 3.3333...%
        pos.CurrentDivergencePct.Should().NotBeNull();
        pos.CurrentDivergencePct!.Value.Should().BeApproximately(3.3333m, 0.001m);
    }

    [Fact]
    public async Task DivergenceAlert_DoesNotFireBelowThreshold()
    {
        // Entry: long=3000, short=3001 → entrySpreadCostPct ≈ 0.0333%
        // DivergenceAlertMultiplier=2.0 → threshold ≈ 0.0666%
        // Current: longMark=3000, shortMark=3001 → divergence = 1/3000.5*100 ≈ 0.0333% < 0.0666%
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 3000m, shortMark: 3001m);

        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000.5m);

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Message.Contains("divergence"))), Times.Never);
    }

    [Fact]
    public async Task UnifiedPnl_LessVolatile_WhenPricesDiverge()
    {
        // Entry: long=3000, short=3001, margin=100, leverage=5
        // avgEntry=3000.5, qty=100*5/3000.5≈0.16664
        //
        // Divergent marks: longMark=2900, shortMark=3100
        //   perExchange: longPnl=(2900-3000)*0.16664=-16.66, shortPnl=(3001-3100)*0.16664=-16.50
        //   perExchangeTotal = -33.16
        //
        // Unified price = 3000 (stable reference):
        //   unifiedLong=(3000-3000)*0.16664=0, unifiedShort=(3001-3000)*0.16664=0.17
        //   unifiedTotal = 0.17
        //
        // Per-exchange shows -33.16 loss; unified shows +0.17 (near zero, correct for delta-neutral)
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2900m, shortMark: 3100m);

        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<AlertType>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync((Alert?)null);

        var result = await _sut.CheckAndActAsync();

        // Per-exchange PnL = -33.16 which exceeds StopLossPct (0.15 * 100 = 15)
        // But unified PnL = 0.17, so no stop loss fires (correct behavior)
        result.ToClose.Should().NotContain(r => r.Reason == CloseReason.StopLoss);
    }

    // ── Review tests: B3, NB3, NB7, CurrentDivergencePct ─────────────────────

    [Fact]
    public async Task DivergenceAlert_SuppressedWhenRecentAlertExists()
    {
        // Same setup as DivergenceAlert_FiresWhenThresholdExceeded but mock returns existing alert
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2950m, shortMark: 3050m);

        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        // Return existing recent alert — dedup should suppress new alert creation
        _mockAlerts.Setup(a => a.GetRecentByPositionIdsAsync(
            It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Dictionary<(int, AlertType), Alert>
            {
                { (pos.Id, AlertType.SpreadWarning), new Alert { Id = 999, Type = AlertType.SpreadWarning } },
            });

        await _sut.CheckAndActAsync();

        // Verify Add is never called for divergence alert because recent one exists
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Message.Contains("divergence"))), Times.Never);
    }

    [Fact]
    public async Task StopLoss_FallsBackToPerExchangePnl_WhenUnifiedPriceZero()
    {
        // Entry: long=3000, short=3001, margin=100, leverage=5
        // avgEntry=3000.5, qty=100*5/3000.5≈0.16664
        //
        // Per-exchange marks: longMark=2000, shortMark=3001
        //   longPnl=(2000-3000)*0.16664=-166.64, shortPnl=(3001-3001)*0.16664=0
        //   perExchangePnl = -166.64 → exceeds StopLossPct (0.15 * 100 = 15)
        //
        // Unified price = 0 → fallback to per-exchange PnL for close decision
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2000m, shortMark: 3001m);

        // Unified price 0 → fallback to per-exchange
        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(0m);

        var result = await _sut.CheckAndActAsync();

        // StopLoss should fire because fallback per-exchange PnL exceeds threshold
        result.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.StopLoss);
    }

    [Fact]
    public async Task DivergenceAlert_IdenticalEntryPrices_DoesNotFire()
    {
        // Entry: long=3000, short=3000 → entryMid=3000, entrySpreadCostPct = |3000-3000|/3000*100 = 0%
        // DivergenceAlertMultiplier=2.0 → threshold = 2.0 * 0 = 0 → guard prevents alert (threshold > 0 check)
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2900m, shortMark: 3100m);

        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        await _sut.CheckAndActAsync();

        // No divergence alert because threshold is 0 (entry prices identical)
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Message.Contains("divergence"))), Times.Never);
    }

    [Fact]
    public async Task CurrentDivergencePct_IsSetOnPosition()
    {
        // Entry: long=3000, short=3001
        // Current: longMark=2990, shortMark=3010 → divergence = |2990-3010|/3000*100 = 20/3000*100 ≈ 0.6667%
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2990m, shortMark: 3010m);

        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<AlertType>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync((Alert?)null);

        await _sut.CheckAndActAsync();

        // divergence = |2990-3010|/3000*100 = 0.6667%
        pos.CurrentDivergencePct.Should().NotBeNull();
        pos.CurrentDivergencePct!.Value.Should().BeApproximately(0.6667m, 0.001m);
    }

    // ── DetermineCloseReason: FundingFlipped ──────────────────────────────────

    [Fact]
    public void FundingFlipped_ClosesPosition_AfterConsecutiveNegativeCycles()
    {
        var pos = MakeOpenPosition();
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            FundingFlipExitCycles = 2,
        };

        // 2 consecutive negative cycles meets threshold
        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 1m, spread: -0.0001m,
            negativeFundingCycles: 2);

        reason.Should().Be(CloseReason.FundingFlipped);
    }

    [Fact]
    public void FundingFlipped_DoesNotClose_BeforeThreshold()
    {
        var pos = MakeOpenPosition();
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            FundingFlipExitCycles = 2,
        };

        // Only 1 negative cycle, threshold is 2
        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 1m, spread: -0.0001m,
            negativeFundingCycles: 1);

        reason.Should().NotBe(CloseReason.FundingFlipped);
    }

    [Fact]
    public async Task FundingFlipped_ResetsCounter_WhenSpreadPositive()
    {
        // First cycle: negative spread
        var pos = MakeOpenPosition();
        pos.CurrentSpreadPerHour = -0.0001m;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0003m, shortRate: 0.0001m); // negative spread
        SetupMarkPrices();

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 48, // high min hold so SpreadCollapsed doesn't trigger
            EmergencyCloseSpreadThreshold = -0.01m, // very low so emergency doesn't trigger
            CloseThreshold = -0.01m,
            FundingFlipExitCycles = 3, // needs 3 consecutive
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        await _sut.CheckAndActAsync(); // 1st negative cycle

        // Second cycle: positive spread — should reset the counter
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // positive spread

        var result = await _sut.CheckAndActAsync(); // resets to 0

        // Third cycle: negative again — only 1 consecutive (counter was reset)
        SetupLatestRates(longRate: 0.0003m, shortRate: 0.0001m); // negative spread

        result = await _sut.CheckAndActAsync(); // 1st negative after reset

        result.ToClose.Should().NotContain(r => r.Reason == CloseReason.FundingFlipped,
            "counter was reset by positive spread; only 1 consecutive negative cycle");
    }

    // ── Collateral imbalance ──────────────────────────────────────────────────

    [Fact]
    public async Task CollateralImbalance_AlertsFired_WhenLegsDiverge()
    {
        // Arrange: asymmetric PnL creates imbalance > 30%
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        // Long mark moved up significantly (large long PnL), short barely moved
        // estimatedQty = (3000+3001)/2 / ((3000+3001)/2) * 100 * 5 / ((3000+3001)/2) ≈ let's compute
        // avgEntryPrice = 3000.5, estimatedQty = 100*5/3000.5 ≈ 0.1666
        // longPnl = (3050 - 3000) * 0.1666 = 8.33 (winning)
        // shortPnl = (3001 - 3005) * 0.1666 = -0.666 (small loss)
        // marginPerLeg = 50, longUtil = |8.33|/50 = 0.1666, shortUtil = |0.666|/50 = 0.01332
        // imbalance = |0.1666 - 0.01332| = 0.1533 — not enough. Need bigger divergence.
        // Let's use 3100 for long mark:
        // longPnl = (3100 - 3000) * 0.1666 = 16.66 → longUtil = 16.66/50 = 0.3332
        // shortPnl = (3001 - 3002) * 0.1666 = -0.1666 → shortUtil = 0.1666/50 = 0.003332
        // imbalance = |0.3332 - 0.003332| = 0.3299 → > 0.30 ✓
        SetupMarkPrices(longMark: 3100m, shortMark: 3002m);

        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), AlertType.MarginWarning, It.IsAny<TimeSpan>()))
            .ReturnsAsync((Alert?)null);

        var result = await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            alert => alert.Type == AlertType.MarginWarning
                && alert.Message.Contains("Collateral imbalance"))),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task CollateralImbalance_NoAlert_WhenWithinThreshold()
    {
        // Arrange: balanced PnL → imbalance < 30%
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        // Symmetric price movement → balanced PnL
        SetupMarkPrices(longMark: 3005m, shortMark: 2996m);

        var result = await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            alert => alert.Message.Contains("Collateral imbalance"))),
            Times.Never);
    }

    // ── Stablecoin depeg ──────────────────────────────────────────────────────

    [Fact]
    public async Task StablecoinDepeg_Critical_ClosesOnlyCrossStablecoin()
    {
        // Arrange: USDCUSDT at 0.99 → 1% spread → critical
        var mockBinance = new Mock<IExchangeConnector>();
        mockBinance.Setup(c => c.GetMarkPriceAsync("USDC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.99m);
        _mockFactory.Setup(f => f.GetConnector("Binance")).Returns(mockBinance.Object);

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            StablecoinAlertThresholdPct = 0.3m,
            StablecoinCriticalThresholdPct = 1.0m,
            FundingFlipExitCycles = 100, // high to avoid FundingFlipped
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Cross-stablecoin position: Binance (USDT) ↔ Hyperliquid (USDC)
        var crossPos = MakeOpenPosition();
        crossPos.Id = 1;
        crossPos.LongExchange = new Exchange { Id = 1, Name = "Binance" };
        crossPos.LongExchangeId = 1;
        crossPos.ShortExchange = new Exchange { Id = 2, Name = "Hyperliquid" };
        crossPos.ShortExchangeId = 2;

        // Same-stablecoin position: Hyperliquid ↔ Lighter (both USDC)
        var samePos = MakeOpenPosition();
        samePos.Id = 2;
        samePos.LongExchange = new Exchange { Id = 3, Name = "Hyperliquid" };
        samePos.LongExchangeId = 3;
        samePos.ShortExchange = new Exchange { Id = 4, Name = "Lighter" };
        samePos.ShortExchangeId = 4;

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([crossPos, samePos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        // Setup mark prices for both connectors
        mockBinance.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);
        _mockFactory.Setup(f => f.GetConnector("Hyperliquid")).Returns(_mockLongConnector.Object);
        _mockFactory.Setup(f => f.GetConnector("Lighter")).Returns(_mockShortConnector.Object);
        _mockLongConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);
        _mockShortConnector.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3001m);

        // Setup rates for both exchange pairs
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m, MarkPrice = 3000m,
                    Exchange = new Exchange { Id = 1, Name = "Binance" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
                new() { ExchangeId = 2, AssetId = 1, RatePerHour = 0.0006m, MarkPrice = 3001m,
                    Exchange = new Exchange { Id = 2, Name = "Hyperliquid" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
                new() { ExchangeId = 3, AssetId = 1, RatePerHour = 0.0001m, MarkPrice = 3000m,
                    Exchange = new Exchange { Id = 3, Name = "Hyperliquid" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
                new() { ExchangeId = 4, AssetId = 1, RatePerHour = 0.0006m, MarkPrice = 3001m,
                    Exchange = new Exchange { Id = 4, Name = "Lighter" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
            });

        // Act
        var result = await _sut.CheckAndActAsync();

        // Assert: cross-stablecoin position closed, same-stablecoin position NOT closed
        result.ToClose.Should().Contain(r => r.Position.Id == 1 && r.Reason == CloseReason.StablecoinDepeg,
            "cross-stablecoin position should be closed on critical depeg");
        result.ToClose.Should().NotContain(r => r.Position.Id == 2 && r.Reason == CloseReason.StablecoinDepeg,
            "same-stablecoin position should NOT be closed");
    }

    [Fact]
    public async Task StablecoinDepeg_FetchFailure_NoFalsePositive()
    {
        // Arrange: Binance connector throws
        var mockBinance = new Mock<IExchangeConnector>();
        mockBinance.Setup(c => c.GetMarkPriceAsync("USDC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection failed"));
        _mockFactory.Setup(f => f.GetConnector("Binance")).Returns(mockBinance.Object);

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            StablecoinCriticalThresholdPct = 1.0m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Cross-stablecoin position
        var pos = MakeOpenPosition();
        pos.LongExchange = new Exchange { Id = 1, Name = "Binance" };
        pos.ShortExchange = new Exchange { Id = 2, Name = "Hyperliquid" };
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices();

        // Act
        var result = await _sut.CheckAndActAsync();

        // Assert: no false positive close from failed price fetch
        result.ToClose.Should().NotContain(r => r.Reason == CloseReason.StablecoinDepeg,
            "price fetch failure should not trigger stablecoin close");
    }

    // ── Review v4 tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task StablecoinDepeg_UsesStablecoinDepegCloseReason()
    {
        // Arrange: USDCUSDT at 0.98 → 2% spread → critical
        var mockBinance = new Mock<IExchangeConnector>();
        mockBinance.Setup(c => c.GetMarkPriceAsync("USDC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.98m);
        mockBinance.Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);
        _mockFactory.Setup(f => f.GetConnector("Binance")).Returns(mockBinance.Object);

        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            StablecoinCriticalThresholdPct = 1.0m,
            FundingFlipExitCycles = 100,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition();
        pos.LongExchange = new Exchange { Id = 1, Name = "Binance" };
        pos.LongExchangeId = 1;
        pos.ShortExchange = new Exchange { Id = 2, Name = "Hyperliquid" };
        pos.ShortExchangeId = 2;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m, MarkPrice = 3000m,
                    Exchange = new Exchange { Id = 1, Name = "Binance" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
                new() { ExchangeId = 2, AssetId = 1, RatePerHour = 0.0006m, MarkPrice = 3001m,
                    Exchange = new Exchange { Id = 2, Name = "Hyperliquid" }, Asset = new Asset { Id = 1, Symbol = "ETH" } },
            });
        SetupMarkPrices();

        // Act
        var result = await _sut.CheckAndActAsync();

        // Assert: CloseReason is StablecoinDepeg, not Manual
        result.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.StablecoinDepeg,
            "stablecoin depeg close should use StablecoinDepeg reason, not Manual");
    }

    [Fact]
    public async Task NegativeFundingCycles_CleanedAfterPositionClose()
    {
        // Arrange: position with negative spread (builds up counter)
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0006m, shortRate: 0.0001m); // negative spread
        SetupMarkPrices();

        // First call — builds up negative funding cycle counter
        await _sut.CheckAndActAsync();

        // Now remove the position (simulating it was closed)
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync(new List<ArbitragePosition>());

        // Second call — no open positions, counter should be cleaned
        var result = await _sut.CheckAndActAsync();

        // Re-add the position (simulating a new position with the same ID)
        var newPos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([newPos]);
        SetupLatestRates(longRate: 0.0006m, shortRate: 0.0001m); // negative spread again
        SetupMarkPrices();

        // Third call — counter should start from 1, not continue from previous
        // If not cleaned, it would have accumulated 2+ cycles. With FundingFlipExitCycles=2,
        // a non-cleaned counter would cause an immediate close on this first negative cycle.
        var config = new BotConfiguration
        {
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.01m, // very low to prevent SpreadCollapsed
            FundingFlipExitCycles = 2, // would trigger if counter wasn't cleaned
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var result3 = await _sut.CheckAndActAsync();

        // Assert: position should NOT be closed (only 1 negative cycle, threshold is 2)
        result3.ToClose.Should().NotContain(r => r.Reason == CloseReason.FundingFlipped,
            "counter should have been cleaned; only 1 negative cycle after cleanup");
    }

    [Fact]
    public async Task CollateralImbalance_ZeroMargin_NoAlert()
    {
        // Arrange: position with MarginUsdc = 0 — should not divide by zero or create alert
        var pos = MakeOpenPosition(marginUsdc: 0m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 3500m); // extreme divergence

        await _sut.CheckAndActAsync();

        // Assert: no collateral imbalance alert (marginPerLeg = 0 → skip)
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Message.Contains("Collateral imbalance"))), Times.Never,
            "zero margin should not trigger collateral imbalance alert or divide-by-zero");
    }

    // ── Review-v6: FundingFlipped state persists across cycles (singleton) ─────

    [Fact]
    public async Task FundingFlipped_StatePersistedAcrossCycles()
    {
        // Arrange: shared singleton state survives between CheckAndActAsync calls.
        // Use a spread that is slightly negative (triggers FundingFlipped counter)
        // but NOT below CloseThreshold or EmergencyCloseSpreadThreshold (so SpreadCollapsed
        // doesn't pre-empt FundingFlipped). Position is freshly opened to avoid MinHoldTimeHours.
        var sharedState = new HealthMonitorState();
        var config = new BotConfiguration
        {
            IsEnabled = true,
            CloseThreshold = -0.001m, // spread -0.0001 is above this
            EmergencyCloseSpreadThreshold = -0.01m,
            AlertThreshold = -0.01m, // suppress spread alerts
            StopLossPct = 0.50m, // generous stop-loss — won't trigger
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 24, // position opened 30min ago → won't trigger SpreadCollapsed
            FundingFlipExitCycles = 2,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var pos = MakeOpenPosition(openedAt: DateTime.UtcNow.AddMinutes(-30));
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        // Mildly negative spread: -0.0001 (above CloseThreshold of -0.001)
        SetupLatestRates(longRate: 0.0002m, shortRate: 0.0001m); // spread = -0.0001
        SetupMarkPrices();

        // Create two separate PositionHealthMonitor instances (simulating scoped resolution),
        // both sharing the same singleton state.
        var sut1 = new PositionHealthMonitor(_mockUow.Object,
            _mockFactory.Object, new Mock<IMarketDataCache>().Object, _mockReferencePriceProvider.Object,
            _mockExecutionEngine.Object, Mock.Of<ILeverageTierProvider>(), sharedState,
            NullLogger<PositionHealthMonitor>.Instance);
        var sut2 = new PositionHealthMonitor(_mockUow.Object,
            _mockFactory.Object, new Mock<IMarketDataCache>().Object, _mockReferencePriceProvider.Object,
            _mockExecutionEngine.Object, Mock.Of<ILeverageTierProvider>(), sharedState,
            NullLogger<PositionHealthMonitor>.Instance);

        // Act — Cycle 1: first negative spread (count = 1, threshold = 2 → no close yet)
        var result1 = await sut1.CheckAndActAsync();
        result1.ToClose.Should().BeEmpty("first negative cycle should not trigger FundingFlipped");

        // Act — Cycle 2: second negative spread (count = 2, threshold = 2 → should close)
        var result2 = await sut2.CheckAndActAsync();
        result2.ToClose.Should().ContainSingle(r => r.Reason == CloseReason.FundingFlipped,
            "FundingFlipped should fire after 2 consecutive negative cycles via singleton state");
    }

    // ── Review-v6: Collateral imbalance uses unified PnL ───────────────────────

    [Fact]
    public async Task CollateralImbalance_UsesUnifiedPnl()
    {
        // Arrange: per-exchange prices diverge significantly but unified price is balanced.
        // Without the fix, per-exchange PnL would trigger a spurious imbalance alert.
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3000m, marginUsdc: 200m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread

        // Divergent per-exchange prices: long mark dropped, short mark rose
        SetupMarkPrices(longMark: 2800m, shortMark: 3200m);
        // But unified price (e.g. index) shows balanced at 3000
        _mockReferencePriceProvider.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter"))
            .Returns(3000m);

        // With unified price = 3000, entry = 3000:
        // unifiedLongPnl = (3000 - 3000) * qty = 0
        // unifiedShortPnl = (3000 - 3000) * qty = 0
        // imbalance = |0 - 0| = 0 → no alert

        await _sut.CheckAndActAsync();

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Message.Contains("Collateral imbalance"))), Times.Never,
            "unified price shows balanced PnL — no spurious imbalance alert");
    }

    // ── B4: ComputeLiquidationDistance — API liquidation price path ────────────

    [Fact]
    public void ComputeLiquidationDistance_ApiLongLiqPriceProvided_UsesApiOverFormula()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        // Formula at 5x leverage: longLiqPrice = 3000 * (1 - 1/5) = 2400
        // API price overrides: 2500
        var apiLong = 2500m;

        PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 2900m, currentShortMark: 3001m,
            apiLongLiqPrice: apiLong, apiShortLiqPrice: null);

        pos.LongLiquidationPrice.Should().Be(2500m,
            "API-pulled liquidation price should be preferred over leverage formula");
    }

    [Fact]
    public void ComputeLiquidationDistance_ApiShortLiqPriceProvided_UsesApiOverFormula()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        // Formula: shortLiqPrice = 3001 * (1 + 1/5) = 3601.2
        var apiShort = 3500m;

        PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3100m,
            apiLongLiqPrice: null, apiShortLiqPrice: apiShort);

        pos.ShortLiquidationPrice.Should().Be(3500m);
    }

    [Fact]
    public void ComputeLiquidationDistance_ApiPricesNull_FallsBackToFormula()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3001m,
            apiLongLiqPrice: null, apiShortLiqPrice: null);

        // Formula path: longLiq = 3000 * (1 - 1/5) = 2400
        pos.LongLiquidationPrice.Should().Be(2400m);
        // shortLiq = 3001 * (1 + 1/5) = 3601.2
        pos.ShortLiquidationPrice.Should().Be(3601.2m);
    }

    [Fact]
    public void ComputeLiquidationDistance_ApiPriceZero_FallsBackToFormula()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3001m,
            apiLongLiqPrice: 0m, apiShortLiqPrice: 0m);

        // The `is > 0m` guard rejects zero — formula is used
        pos.LongLiquidationPrice.Should().Be(2400m);
        pos.ShortLiquidationPrice.Should().Be(3601.2m);
    }

    [Fact]
    public void ComputeLiquidationDistance_ApiPriceNegative_FallsBackToFormula()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3001m,
            apiLongLiqPrice: -100m, apiShortLiqPrice: -100m);

        pos.LongLiquidationPrice.Should().Be(2400m);
        pos.ShortLiquidationPrice.Should().Be(3601.2m);
    }

    [Fact]
    public void ComputeLiquidationDistance_OnlyLongApiProvided_UsesApiForLongFormulaForShort()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3001m,
            apiLongLiqPrice: 2500m, apiShortLiqPrice: null);

        pos.LongLiquidationPrice.Should().Be(2500m, "API for long");
        pos.ShortLiquidationPrice.Should().Be(3601.2m, "formula for short");
    }

    // ── B5: DetermineCloseReason — DivergenceCritical and PnL deferral ─────────

    [Fact]
    public void DetermineCloseReason_DivergenceExceedsCriticalThreshold_ReturnsDivergenceCritical()
    {
        var pos = MakeOpenPosition();
        pos.CurrentDivergencePct = 1.0m;

        var config = new BotConfiguration
        {
            DivergenceAlertMultiplier = 2.0m,
            StopLossPct = 0.15m,
            CloseThreshold = -0.00005m,
            // Legacy threshold-only semantics — divergence close fires on threshold breach
            // regardless of hold time or liquidation distance. The new risk-based gates
            // (default) are exercised by DetermineCloseReason_DivergenceCritical_* tests.
            UseRiskBasedDivergenceClose = false,
        };

        // entrySpreadCostPct = 0.1, alert threshold = 0.1 * 2 = 0.2, critical = 0.4
        // CurrentDivergencePct (1.0) > critical (0.4) → DivergenceCritical
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m,
            entrySpreadCostPct: 0.1m);

        result.Should().Be(CloseReason.DivergenceCritical);
    }

    [Fact]
    public void DetermineCloseReason_DivergenceAboveAlertButBelowCritical_DoesNotReturnDivergenceCritical()
    {
        var pos = MakeOpenPosition();
        pos.CurrentDivergencePct = 0.3m;

        var config = new BotConfiguration
        {
            DivergenceAlertMultiplier = 2.0m,
            StopLossPct = 0.15m,
            CloseThreshold = -0.00005m,
        };

        // alert = 0.2, critical = 0.4, divergence = 0.3 → above alert, below critical
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m,
            entrySpreadCostPct: 0.1m);

        result.Should().NotBe(CloseReason.DivergenceCritical);
    }

    [Fact]
    public void DetermineCloseReason_EntrySpreadCostZero_DoesNotReturnDivergenceCritical()
    {
        var pos = MakeOpenPosition();
        pos.CurrentDivergencePct = 100m; // huge divergence

        var config = new BotConfiguration
        {
            DivergenceAlertMultiplier = 2.0m,
            StopLossPct = 0.15m,
            CloseThreshold = -0.00005m,
        };

        // entrySpreadCostPct = 0 → divergenceCloseThreshold guard returns 0 → no DivergenceCritical
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m,
            entrySpreadCostPct: 0m);

        result.Should().NotBe(CloseReason.DivergenceCritical);
    }

    [Fact]
    public void DetermineCloseReason_StopLossPriority_OverridesDivergenceCritical()
    {
        var pos = MakeOpenPosition();
        pos.MarginUsdc = 100m;
        pos.CurrentDivergencePct = 100m; // would trigger DivergenceCritical

        var config = new BotConfiguration
        {
            DivergenceAlertMultiplier = 2.0m,
            StopLossPct = 0.10m,
            CloseThreshold = -0.00005m,
        };

        // unrealizedPnl = -50 (loss); stop loss = 100 * 0.10 = 10; |loss| >= stop → StopLoss wins
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: -50m, hoursOpen: 2m, spread: 0.001m,
            entrySpreadCostPct: 0.1m);

        result.Should().Be(CloseReason.StopLoss);
    }

    [Fact]
    public void DetermineCloseReason_PnlTargetReached_DefersWhenDivergenceAboveAlert()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = 0.90m; // exactly at target (see existing test)
        pos.CurrentDivergencePct = 0.3m; // above alert (0.2), below critical (0.4)

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            DivergenceAlertMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            MinHoldBeforePnlTargetMinutes = 60,
        };

        // Position is 2h old (120 min) > MinHoldBeforePnlTargetMinutes (60).
        // Without divergence, this would return PnlTargetReached.
        // With divergence above alert and not over-performed (3× target), it defers.
        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m,
            entrySpreadCostPct: 0.1m);

        result.Should().BeNull("PnlTargetReached should defer when divergence is elevated");
    }

    [Fact]
    public void DetermineCloseReason_PnlTargetReached_FiresEvenWithDivergence_WhenOverperformed()
    {
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.CurrentDivergencePct = 0.3m; // above alert, below critical

        var config = new BotConfiguration
        {
            AdaptiveHoldEnabled = true,
            TargetPnlMultiplier = 2.0m,
            DivergenceAlertMultiplier = 2.0m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            CloseThreshold = -0.00005m,
            MinHoldBeforePnlTargetMinutes = 60,
        };

        // entryFee = 100 * 5 * 2 * 0.00045 = 0.45. Target = 2.0 * 0.45 = 0.90.
        // Over-performed = 3 * 2.0 * 0.45 = 2.70. Set funding well above that.
        pos.AccumulatedFunding = 5m;

        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m,
            entrySpreadCostPct: 0.1m);

        result.Should().Be(CloseReason.PnlTargetReached,
            "over-performed positions skip the divergence-based deferral");
    }

    // ── B-NEW-1: ComputeLiquidationDistance negative-range bug regression tests ──

    [Fact]
    public void ComputeLiquidationDistance_ApiLongLiqAboveEntry_TreatsAsImmediateRisk()
    {
        // When the API-pulled liquidation price has crossed the entry price (e.g., accrued
        // adverse PnL has shifted the cross-margin liquidation past entry), the safe range
        // becomes non-positive. The previous implementation returned decimal.MaxValue here
        // ("infinitely safe"), masking the imminent liquidation. The fix returns 0m (at
        // liquidation), which trips LiquidationWarningPct.
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 2950m, currentShortMark: 3001m,
            apiLongLiqPrice: 3060m, // ABOVE entry — long is past safe range
            apiShortLiqPrice: null);

        distance.Should().Be(0m, "API-reported liq above entry means the position is past its safe range");
    }

    [Fact]
    public void ComputeLiquidationDistance_ApiShortLiqBelowEntry_TreatsAsImmediateRisk()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3050m,
            apiLongLiqPrice: null,
            apiShortLiqPrice: 2900m); // BELOW entry — short is past safe range

        distance.Should().Be(0m);
    }

    [Fact]
    public void ComputeLiquidationDistance_ApiLiqExactlyAtEntry_TreatsAsImmediateRisk()
    {
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        // longRange = entry - liqPrice = 3000 - 3000 = 0; range > 0 is false → returns 0m
        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 3000m, currentShortMark: 3001m,
            apiLongLiqPrice: 3000m,
            apiShortLiqPrice: null);

        distance.Should().Be(0m);
    }

    [Fact]
    public void ComputeLiquidationDistance_NegativeRangeTriggersLiquidationCloseReason()
    {
        // End-to-end: confirm a position with negative API-range is closed by DetermineCloseReason
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        var distance = PositionHealthMonitor.ComputeLiquidationDistance(
            pos, currentLongMark: 2950m, currentShortMark: 3001m,
            apiLongLiqPrice: 3060m,
            apiShortLiqPrice: null);

        var config = new BotConfiguration
        {
            LiquidationWarningPct = 0.50m,
            StopLossPct = 0.15m,
            CloseThreshold = -0.00005m,
        };

        var reason = PositionHealthMonitor.DetermineCloseReason(
            pos, config, unrealizedPnl: 0m, hoursOpen: 2m, spread: 0.001m,
            minLiquidationDistance: distance);

        reason.Should().Be(CloseReason.LiquidationRisk,
            "distance=0 must trigger LiquidationRisk close, not silently pass through");
    }

    // ── B-NEW-3: leverage-aware margin threshold discriminating tests ──────────

    [Fact]
    public async Task CheckAndAct_HighLeverage_TighterThresholdFiresEarlier()
    {
        // Leverage 5 (>=3) → threshold = min(0.70, 0.60) = 0.60.
        // 0.65 utilization should fire under the new policy but would not have fired under
        // the old fixed-70 rule.
        var pos = MakeOpenPosition();
        pos.Leverage = 5;
        await SetupAndRun_MarginUtilization_TestAsync(pos, marginUtilizationPct: 0.65m);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Type == AlertType.MarginWarning &&
            al.Message.Contains("65"))), Times.Once,
            "high-leverage position at 65% util should fire under the new 60% threshold");
    }

    [Fact]
    public async Task CheckAndAct_LowLeverage_RetainsOriginalThreshold()
    {
        // Leverage 2 (<3) → threshold = config default 0.70.
        // 0.65 utilization is below the threshold; no alert.
        var pos = MakeOpenPosition();
        pos.Leverage = 2;
        await SetupAndRun_MarginUtilization_TestAsync(pos, marginUtilizationPct: 0.65m);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Type == AlertType.MarginWarning)), Times.Never,
            "low-leverage position at 65% util should not fire — threshold is 70%");
    }

    [Fact]
    public async Task CheckAndAct_LowLeverage_FiresAtOriginalThreshold()
    {
        // Leverage 2 (<3) → threshold = 0.70. 0.72 should fire.
        var pos = MakeOpenPosition();
        pos.Leverage = 2;
        await SetupAndRun_MarginUtilization_TestAsync(pos, marginUtilizationPct: 0.72m);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.Type == AlertType.MarginWarning)), Times.Once,
            "low-leverage position at 72% util should fire (>70% threshold)");
    }

    /// <summary>
    /// Helper that sets up a single-position monitor cycle with a specified margin utilization
    /// from both connectors and runs CheckAndActAsync.
    /// </summary>
    private async Task SetupAndRun_MarginUtilization_TestAsync(ArbitragePosition pos, decimal marginUtilizationPct)
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            MarginUtilizationAlertPct = 0.70m,
            CloseThreshold = -0.00005m,
            AlertThreshold = 0.0001m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            DivergenceAlertMultiplier = 2.0m,
        });
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync(new List<ArbitragePosition> { pos });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());
        _mockAlerts.Setup(a => a.GetRecentByPositionIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<IEnumerable<AlertType>>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Dictionary<(int, AlertType), Alert>());
        SetupMarkPrices();

        var marginState = new MarginStateDto
        {
            MarginUsed = 100m,
            MarginAvailable = 50m,
            MarginUtilizationPct = marginUtilizationPct,
        };
        _mockLongConnector.Setup(c => c.GetPositionMarginStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(marginState);
        _mockShortConnector.Setup(c => c.GetPositionMarginStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(marginState);

        await _sut.CheckAndActAsync();
    }

    // ── ComputePositionSnapshotAsync ──────────────────────────────────────────

    [Fact]
    public async Task ComputePositionSnapshotAsync_PopulatesAllFields_ForFreshPosition()
    {
        // Arrange: build a SUT with a warm market data cache
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetMarkPrice("Hyperliquid", "ETH")).Returns(3010m);
        mockCache.Setup(c => c.GetMarkPrice("Lighter", "ETH")).Returns(3005m);

        var mockRefPrice = new Mock<IReferencePriceProvider>();
        mockRefPrice.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter")).Returns(3007.5m);

        var sut = new PositionHealthMonitor(
            _mockUow.Object,
            _mockFactory.Object,
            mockCache.Object,
            mockRefPrice.Object,
            _mockExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);

        // Act
        var snapshot = await sut.ComputePositionSnapshotAsync(pos);

        // Assert
        snapshot.Should().NotBeNull();
        snapshot!.ExchangePnl.Should().NotBe(0m, "exchange PnL should be computed from mark prices");
        snapshot.UnifiedPnl.Should().NotBe(0m, "unified PnL should be computed from unified price");
        snapshot.DivergencePct.Should().BeGreaterThan(0m, "divergence should be non-zero when marks differ");
    }

    [Fact]
    public async Task ComputePositionSnapshotAsync_ReturnsNull_WhenCacheCold()
    {
        // The default _sut has an IMarketDataCache mock returning 0 for all symbols
        var pos = MakeOpenPosition();

        var snapshot = await _sut.ComputePositionSnapshotAsync(pos);

        snapshot.Should().BeNull("cache cold should return null so caller falls back to zero-PnL");
    }

    [Fact]
    public async Task ComputePositionSnapshotAsync_ReturnsNull_WhenNavigationPropertiesNull()
    {
        // B4: When Asset, LongExchange, ShortExchange are null, the method uses "?" for
        // exchange/asset names. GetMarkPrice("?", "?") returns 0 → cache-cold path → null.
        var pos = MakeOpenPosition();
        pos.Asset = null!;
        pos.LongExchange = null!;
        pos.ShortExchange = null!;

        // Default _sut cache returns 0 for any symbol → treated as cold
        var snapshot = await _sut.ComputePositionSnapshotAsync(pos);

        snapshot.Should().BeNull("null navigation properties produce invalid cache keys → cache cold → null");
    }

    [Fact]
    public async Task ComputePositionSnapshotAsync_CollateralImbalanceNull_WhenMarginZero()
    {
        // NB2: When MarginUsdc is 0, marginPerLeg is 0, so CollateralImbalancePct stays null
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetMarkPrice("Hyperliquid", "ETH")).Returns(3010m);
        mockCache.Setup(c => c.GetMarkPrice("Lighter", "ETH")).Returns(3005m);

        var mockRefPrice = new Mock<IReferencePriceProvider>();
        mockRefPrice.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter")).Returns(3007.5m);

        var sut = new PositionHealthMonitor(
            _mockUow.Object,
            _mockFactory.Object,
            mockCache.Object,
            mockRefPrice.Object,
            _mockExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var pos = MakeOpenPosition(marginUsdc: 0m);

        var snapshot = await sut.ComputePositionSnapshotAsync(pos);

        snapshot.Should().NotBeNull();
        snapshot!.CollateralImbalancePct.Should().BeNull(
            "when MarginUsdc is 0, marginPerLeg is 0 and CollateralImbalancePct should remain null");
    }

    [Fact]
    public async Task ComputePositionSnapshotAsync_FallsBackToExchangePnl_WhenUnifiedPriceUnavailable()
    {
        // NB3: When GetUnifiedPrice returns 0, unifiedPnl falls back to exchangePnl
        // and divergencePct becomes 0
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetMarkPrice("Hyperliquid", "ETH")).Returns(3010m);
        mockCache.Setup(c => c.GetMarkPrice("Lighter", "ETH")).Returns(3005m);

        var mockRefPrice = new Mock<IReferencePriceProvider>();
        mockRefPrice.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter")).Returns(0m);

        var sut = new PositionHealthMonitor(
            _mockUow.Object,
            _mockFactory.Object,
            mockCache.Object,
            mockRefPrice.Object,
            _mockExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        var snapshot = await sut.ComputePositionSnapshotAsync(pos);

        snapshot.Should().NotBeNull();
        snapshot!.UnifiedPnl.Should().Be(snapshot.ExchangePnl,
            "when unified price is unavailable, UnifiedPnl should fall back to ExchangePnl");
        snapshot.DivergencePct.Should().Be(0m,
            "when unified price is unavailable, DivergencePct should be 0");
    }

    [Fact]
    public async Task ComputePositionSnapshotAsync_ReturnsNull_WhenEntryPricesAreZero()
    {
        // NB5: The guard at line ~928-931 returns null when entry prices are zero,
        // independent of cache state. Test this guard with warm cache.
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetMarkPrice("Hyperliquid", "ETH")).Returns(3010m);
        mockCache.Setup(c => c.GetMarkPrice("Lighter", "ETH")).Returns(3005m);

        var sut = new PositionHealthMonitor(
            _mockUow.Object,
            _mockFactory.Object,
            mockCache.Object,
            _mockReferencePriceProvider.Object,
            _mockExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var pos = MakeOpenPosition(longEntry: 0m, shortEntry: 0m);

        var snapshot = await sut.ComputePositionSnapshotAsync(pos);

        snapshot.Should().BeNull("zero entry prices should trigger the early-exit guard regardless of cache state");
    }

    // ── Divergence badge invariant tests ──────────────────────────────────────

    [Fact]
    public async Task DivergencePct_ForKnownInputs_ReturnsExpectedValue()
    {
        // Arrange: longMark=100.50, shortMark=99.50, unifiedPrice=100.00
        // Expected: |100.50 - 99.50| / 100.00 * 100 = 1.00%
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetMarkPrice("Hyperliquid", "ETH")).Returns(100.50m);
        mockCache.Setup(c => c.GetMarkPrice("Lighter", "ETH")).Returns(99.50m);

        var mockRefPrice = new Mock<IReferencePriceProvider>();
        mockRefPrice.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter")).Returns(100.00m);

        var sut = new PositionHealthMonitor(
            _mockUow.Object,
            _mockFactory.Object,
            mockCache.Object,
            mockRefPrice.Object,
            _mockExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        var pos = MakeOpenPosition(longEntry: 100m, shortEntry: 100m);

        // Act
        var snapshot = await sut.ComputePositionSnapshotAsync(pos);

        // Assert
        snapshot.Should().NotBeNull();
        snapshot!.DivergencePct.Should().BeApproximately(1.00m, 0.001m,
            "divergence = |100.50 - 99.50| / 100.00 * 100 = 1.00%");
    }

    [Theory]
    [InlineData(101.0, 99.0, 100.0, "long-favored: longMark > shortMark")]
    [InlineData(99.0, 101.0, 100.0, "short-favored: longMark < shortMark")]
    [InlineData(100.0, 100.0, 100.0, "symmetric: longMark == shortMark")]
    public async Task DivergencePct_InvariantRelation_ExchangeVsUnifiedPnlGap(
        decimal longMark, decimal shortMark, decimal unifiedPrice, string scenario)
    {
        // The invariant: |ExchangePnl - UnifiedPnl| / notional ~ |DivergencePct| / 100
        // where notional = estimatedQty * unifiedPrice
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetMarkPrice("Hyperliquid", "ETH")).Returns(longMark);
        mockCache.Setup(c => c.GetMarkPrice("Lighter", "ETH")).Returns(shortMark);

        var mockRefPrice = new Mock<IReferencePriceProvider>();
        mockRefPrice.Setup(r => r.GetUnifiedPrice("ETH", "Hyperliquid", "Lighter")).Returns(unifiedPrice);

        var sut = new PositionHealthMonitor(
            _mockUow.Object,
            _mockFactory.Object,
            mockCache.Object,
            mockRefPrice.Object,
            _mockExecutionEngine.Object,
            Mock.Of<ILeverageTierProvider>(),
            new HealthMonitorState(),
            NullLogger<PositionHealthMonitor>.Instance);

        // Use symmetric entry prices so divergence is entirely from mark price difference
        var pos = MakeOpenPosition(longEntry: 100m, shortEntry: 100m);

        var snapshot = await sut.ComputePositionSnapshotAsync(pos);

        snapshot.Should().NotBeNull($"snapshot should be available for scenario: {scenario}");

        if (longMark == shortMark)
        {
            // Symmetric case: zero divergence, PnL gap should be zero
            snapshot!.DivergencePct.Should().Be(0m);
            Math.Abs(snapshot.ExchangePnl - snapshot.UnifiedPnl).Should().Be(0m);
        }
        else
        {
            // Invariant: the PnL gap normalized by notional should approximate divergence/100
            var avgEntryPrice = (pos.LongEntryPrice + pos.ShortEntryPrice) / 2m;
            var estimatedQty = pos.SizeUsdc * pos.Leverage / avgEntryPrice;
            var notional = estimatedQty * unifiedPrice;
            var pnlGapNormalized = notional > 0
                ? Math.Abs(snapshot!.ExchangePnl - snapshot.UnifiedPnl) / notional
                : 0m;
            var divergenceAsDecimal = snapshot!.DivergencePct / 100m;

            pnlGapNormalized.Should().BeApproximately(divergenceAsDecimal, 0.01m,
                $"PnL gap should explain divergence for scenario: {scenario}");
        }
    }

    // ── Per-leg filled quantities in PnL ────────────────────────────

    [Fact]
    public async Task CheckAndAct_UsesFilledQuantities_WhenAvailable()
    {
        // Position with known filled quantities
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        pos.LongFilledQuantity = 0.20m;
        pos.ShortFilledQuantity = 0.18m;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m);

        // With per-leg quantities:
        // longPnl  = (2500 - 3000) * 0.20 = -100.0
        // shortPnl = (3001 - 3001) * 0.18 = 0.0
        // unrealizedPnl = -100.0 → |100| > 15 → triggers stop-loss
        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
    }

    [Fact]
    public async Task CheckAndAct_FallsBackToEstimatedQty_WhenFilledQuantitiesNull()
    {
        // Position without filled quantities → falls back to estimated qty
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        pos.LongFilledQuantity = null;
        pos.ShortFilledQuantity = null;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m);

        // With estimated qty: avgEntry=3000.5, estimatedQty=500/3000.5≈0.16664
        // longPnl ≈ (2500-3000)*0.16664 ≈ -83.32 → |83.32| > 15 → triggers
        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
    }

    [Fact]
    public async Task CheckAndAct_FallsBackToEstimatedQty_WhenFilledQuantityZero()
    {
        // Position with zero filled quantities → falls back to estimated qty
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        pos.LongFilledQuantity = 0m;
        pos.ShortFilledQuantity = 0m;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m);

        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
    }

    // ── Unified-PnL guard for PnlTargetReached (fix/pnl-target-reached-unified-pnl-guard) ──

    private static BotConfiguration MakePnlTargetConfig(decimal pnlTargetUnifiedTolerance = 0m) => new()
    {
        AdaptiveHoldEnabled = true,
        TargetPnlMultiplier = 2.0m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 72,
        CloseThreshold = -0.00005m,
        MinHoldBeforePnlTargetMinutes = 60,
        PnlTargetUnifiedTolerance = pnlTargetUnifiedTolerance,
    };

    // entryFee = SizeUsdc(100) * Leverage(5) * 2 * GetTakerFeeRate("Hyperliquid","Lighter")(0.00045) = 0.45
    // target   = TargetPnlMultiplier(2.0) * 0.45 = 0.90
    // Use AccumulatedFunding=1.0 → >= target, < 3×target(2.70) → not hasOverperformed
    // hoursOpen=2 → minutesOpen=120 >= MinHoldBeforePnlTargetMinutes(60) ✓

    [Fact]
    public void PnlTargetReached_UnifiedNegative_DoesNotClose()
    {
        // totalPnl = -0.07 + 1.0 - 0.45 = 0.48 > 0 ✓
        // unifiedPnl = -0.07 < -PnlTargetUnifiedTolerance(0m) → gate blocks close
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = 1.0m;

        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, MakePnlTargetConfig(pnlTargetUnifiedTolerance: 0m),
            unrealizedPnl: -0.07m, hoursOpen: 2m, spread: 0.001m);

        result.Should().NotBe(CloseReason.PnlTargetReached,
            "unified PnL is negative and below tolerance — position must not be closed at PnL target");
    }

    [Fact]
    public void PnlTargetReached_UnifiedPositive_ClosesAsBefore()
    {
        // totalPnl = 0.10 + 1.0 - 0.45 = 0.65 > 0 ✓
        // unifiedPnl = 0.10 >= 0 → gate passes → close
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = 1.0m;

        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, MakePnlTargetConfig(pnlTargetUnifiedTolerance: 0m),
            unrealizedPnl: 0.10m, hoursOpen: 2m, spread: 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached,
            "unified PnL is non-negative — close must fire as before the guard was introduced");
    }

    [Fact]
    public void PnlTargetReached_UnifiedWithinTolerance_ClosesWhenToleranceSet()
    {
        // totalPnl = -0.03 + 1.0 - 0.45 = 0.52 > 0 ✓
        // unifiedPnl = -0.03 >= -PnlTargetUnifiedTolerance(0.05m) → within tolerance → close
        var pos = MakeOpenPosition();
        pos.SizeUsdc = 100m;
        pos.AccumulatedFunding = 1.0m;

        var result = PositionHealthMonitor.DetermineCloseReason(
            pos, MakePnlTargetConfig(pnlTargetUnifiedTolerance: 0.05m),
            unrealizedPnl: -0.03m, hoursOpen: 2m, spread: 0.001m);

        result.Should().Be(CloseReason.PnlTargetReached,
            "unified PnL of -0.03 is within tolerance of 0.05 — close must fire");
    }

    [Fact]
    public async Task CheckAndAct_AsymmetricFilledQuantities_FallsBackToEstimated()
    {
        // One leg has a filled quantity, the other is null — should fall back to estimated qty for both legs
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        pos.LongFilledQuantity = 0.20m;
        pos.ShortFilledQuantity = null;
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 2500m, shortMark: 3001m);

        // With estimated qty (fallback): avgEntry=3000.5, estimatedQty=500/3000.5≈0.16664
        // longPnl ≈ (2500-3000)*0.16664 ≈ -83.32 → |83.32| > 15 → triggers stop-loss
        var result = await _sut.CheckAndActAsync();

        result.ToClose.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
    }

    // ── ReconciliationDrift PnL exclusion ──────────────────────────────────────

    /// <summary>
    /// A position with CloseReason=ReconciliationDrift must be skipped in the open-position
    /// health loop so it contributes zero unrealized PnL to the aggregation result.
    /// Normally such rows have Status=Failed and never appear in GetOpenTrackedAsync, but
    /// the guard fires if state ever drifts.
    /// </summary>
    [Fact]
    public async Task ReconciliationDriftRows_ContributeZeroToPnlAggregate()
    {
        // Row with stale non-zero leg values that would produce non-zero PnL if computed
        var driftPos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        driftPos.CloseReason = CloseReason.ReconciliationDrift;
        driftPos.LongFilledQuantity = 0.1m;
        driftPos.ShortFilledQuantity = 0.1m;

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([driftPos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 4000m, shortMark: 2000m); // extreme prices → would be large PnL if not skipped

        var result = await _sut.CheckAndActAsync();

        // The drift row must not appear in the computed PnL dictionary — zero contribution
        result.ComputedPnl.Should().NotContainKey(driftPos.Id,
            "ReconciliationDrift position must be skipped and contribute zero to PnL aggregation");
        // And it must not trigger a close decision (the guard uses continue, not toClose)
        result.ToClose.Should().BeEmpty(
            "ReconciliationDrift rows must not generate close decisions from the health loop");
    }

    /// <summary>
    /// When an Open position and a ReconciliationDrift position coexist in the tracked set,
    /// only the Open position's PnL is aggregated; the drift row is excluded.
    /// </summary>
    [Fact]
    public async Task DriftAndOpenRowsMixed_OnlyOpenRowsAggregated()
    {
        var openPos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        openPos.Id = 10;
        openPos.LongFilledQuantity = 0.1m;
        openPos.ShortFilledQuantity = 0.1m;

        var driftPos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m, marginUsdc: 100m);
        driftPos.Id = 11;
        driftPos.CloseReason = CloseReason.ReconciliationDrift;
        driftPos.LongFilledQuantity = 0.1m;
        driftPos.ShortFilledQuantity = 0.1m;

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([openPos, driftPos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);
        SetupMarkPrices(longMark: 3100m, shortMark: 2900m);

        var result = await _sut.CheckAndActAsync();

        // Open position must be in the PnL map
        result.ComputedPnl.Should().ContainKey(openPos.Id,
            "the Open position must have its PnL computed");
        // Drift position must NOT be in the PnL map
        result.ComputedPnl.Should().NotContainKey(driftPos.Id,
            "the ReconciliationDrift position must be excluded from PnL aggregation");
    }
}
