using FluentAssertions;
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
    private readonly Mock<IExecutionEngine> _mockExecutionEngine = new();
    private readonly PositionHealthMonitor _sut;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        CloseThreshold = -0.00005m,
        AlertThreshold = 0.0001m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 72,
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

        // Default: no stale positions for M4 reaper
        _mockPositions.Setup(p => p.GetByStatusAsync(It.IsAny<PositionStatus>()))
            .ReturnsAsync([]);

        _sut = new PositionHealthMonitor(_mockUow.Object,
            _mockFactory.Object, new Mock<IMarketDataCache>().Object, _mockExecutionEngine.Object,
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

        result.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.SpreadCollapsed);
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

        result.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.MaxHoldTimeReached);
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

        result.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
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

        result.Should().BeEmpty();
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

        // Recent alert exists
        _mockAlerts.Setup(a => a.GetRecentAsync(
            It.IsAny<string>(), It.IsAny<int?>(), AlertType.SpreadWarning, It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Alert
            {
                UserId = "admin-user-id",
                Type = AlertType.SpreadWarning,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                Message = "test",
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
        _mockPositions.Verify(p => p.Update(pos), Times.Once);
    }

    // ── No positions → no work ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenNoOpenPositions_DoesNothing()
    {
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        var result = await _sut.CheckAndActAsync();

        result.Should().BeEmpty();
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
        result.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.SpreadCollapsed);
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

        result.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.StopLoss);
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
        IReadOnlyList<(ArbitragePosition Position, CloseReason Reason)>? result = null;
        var act = async () => { result = await _sut.CheckAndActAsync(); };
        await act.Should().NotThrowAsync();

        // pos1's spread is updated (happens before the try/catch)
        pos1.CurrentSpreadPerHour.Should().Be(0.0005m); // shortRate - longRate = 0.0006 - 0.0001
        // pos2 was fully processed — no close triggered (spread is healthy)
        result.Should().NotBeNull();
        result!.Should().BeEmpty("neither position needs closing with healthy spreads");
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

        result.Should().ContainSingle(r => r.Reason == CloseReason.StopLoss);
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

        result.Should().ContainSingle(r => r.Reason == CloseReason.MaxHoldTimeReached);
    }

    // ── D4: Zero entry prices guard ─────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_ZeroEntryPrices_SkipsPosition()
    {
        var pos = MakeOpenPosition(longEntry: 0m, shortEntry: 0m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        var result = await _sut.CheckAndActAsync();

        result.Should().BeEmpty("position with zero entry prices should be skipped, not closed");
    }

    // ── D4: Price feed failure — 5 consecutive creates alert ────────────────────

    [Fact]
    public async Task CheckAndAct_PriceFeedFailure_5Consecutive_CreatesAlert()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m);

        // Make GetMarkPriceAsync throw every time
        _mockLongConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Run 5 cycles to accumulate failures
        for (int i = 0; i < 5; i++)
        {
            await _sut.CheckAndActAsync();
        }

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Type == AlertType.PriceFeedFailure && al.Severity == AlertSeverity.Critical)),
            Times.Once);
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
        BotOrchestrator.ComputeWarnings(dto, pos, config);

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
        BotOrchestrator.ComputeWarnings(dto, pos, config);

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
        BotOrchestrator.ComputeWarnings(dto, pos, config);

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

        result.Should().ContainSingle(r => r.Position == pos && r.Reason == CloseReason.PnlTargetReached);
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
        BotOrchestrator.ComputeWarnings(dto, pos, config);

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
}
