using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
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
    private readonly Mock<IExecutionEngine> _mockExecEngine = new();
    private readonly PositionHealthMonitor _sut;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        CloseThreshold = -0.00005m,
        AlertThreshold = 0.0001m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 72,
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

        _sut = new PositionHealthMonitor(_mockUow.Object, _mockExecEngine.Object,
            _mockFactory.Object, NullLogger<PositionHealthMonitor>.Instance);
    }

    private ArbitragePosition MakeOpenPosition(
        DateTime? openedAt = null,
        decimal entrySpread = 0.0005m,
        decimal longEntry = 3000m,
        decimal shortEntry = 3001m)
    {
        return new ArbitragePosition
        {
            Id = 1,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
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

        await _sut.CheckAndActAsync();

        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(pos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Auto-close: max hold time ──────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenMaxHoldTimeReached_ClosesPosition()
    {
        var pos = MakeOpenPosition(openedAt: DateTime.UtcNow.AddHours(-73)); // >72 hours
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices();

        await _sut.CheckAndActAsync();

        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(pos, CloseReason.MaxHoldTimeReached, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Auto-close: stop loss ──────────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenStopLossTriggered_ClosesPosition()
    {
        // Entry = 3000/3001. Price moved >15%: 3000 → 3500 = 16.7% move
        var pos = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // healthy spread
        SetupMarkPrices(longMark: 3500m, shortMark: 3500m); // big price move

        await _sut.CheckAndActAsync();

        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(pos, CloseReason.StopLoss, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── No close: healthy spread ───────────────────────────────────────────────

    [Fact]
    public async Task CheckAndAct_WhenSpreadHealthy_DoesNothing()
    {
        var pos = MakeOpenPosition();
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);
        SetupLatestRates(longRate: 0.0001m, shortRate: 0.0006m); // spread = 0.0005 (healthy)
        SetupMarkPrices();

        await _sut.CheckAndActAsync();

        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        await _sut.CheckAndActAsync();

        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        await _sut.CheckAndActAsync();

        // One save for the spread update batch, then close is called
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(pos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()),
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
                    throw new HttpRequestException("Simulated timeout");
                return 3000m;
            });
        _mockShortConnector
            .Setup(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3001m);

        // Act — must not throw
        var act = async () => await _sut.CheckAndActAsync();
        await act.Should().NotThrowAsync();

        // pos1's spread is updated (happens before the try/catch)
        pos1.CurrentSpreadPerHour.Should().Be(0.0005m); // shortRate - longRate = 0.0006 - 0.0001
        // pos2 was fully processed — no close triggered (spread is healthy)
        _mockExecEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // SaveAsync still called once
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
