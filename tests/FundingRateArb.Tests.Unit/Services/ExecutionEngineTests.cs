using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class ExecutionEngineTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IExchangeConnector> _mockShortConnector = new();
    private readonly ExecutionEngine _sut;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        DefaultLeverage = 5,
        UpdatedByUserId = "admin-user-id",
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

    public ExecutionEngineTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);

        _mockFactory.Setup(f => f.GetConnector("Hyperliquid")).Returns(_mockLongConnector.Object);
        _mockFactory.Setup(f => f.GetConnector("Lighter")).Returns(_mockShortConnector.Object);

        // Default: both exchanges have ample balance for pre-flight margin check
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);

        _sut = new ExecutionEngine(_mockUow.Object, _mockFactory.Object, NullLogger<ExecutionEngine>.Instance);
    }

    private static OrderResultDto SuccessOrder(string orderId = "1", decimal price = 3000m, decimal qty = 0.1m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = qty };

    private static OrderResultDto FailOrder(string error = "Insufficient margin") =>
        new() { Success = false, Error = error };

    // ── OpenPositionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_BothLegsSucceed_SavesPosition()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        _mockPositions.Verify(p => p.Add(It.IsAny<ArbitragePosition>()), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OpenPosition_BothLegsSucceed_CreatesPositionOpenedAlert()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.PositionOpened)), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_BothLegsSucceed_SetsCorrectEntryPrices()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 2999m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3002m));

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        savedPos.Should().NotBeNull();
        savedPos!.LongEntryPrice.Should().Be(2999m);
        savedPos.ShortEntryPrice.Should().Be(3002m);
        savedPos.LongOrderId.Should().Be("long-1");
        savedPos.ShortOrderId.Should().Be("short-1");
        savedPos.Status.Should().Be(PositionStatus.Open);
    }

    /// <summary>
    /// C-EE2: Position must be persisted with Opening status BEFORE legs are fired.
    /// Verifies SaveAsync is called once before PlaceMarketOrderAsync is invoked.
    /// </summary>
    [Fact]
    public async Task OpenPosition_PersistedWithOpeningStatus_BeforeLegsAreFired()
    {
        var callOrder = new List<string>();
        PositionStatus? statusAtAddTime = null;

        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(_ => callOrder.Add("SaveAsync"))
            .ReturnsAsync(1);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, CancellationToken>((_, _, _, _, _) => callOrder.Add("LongLeg"))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, CancellationToken>((_, _, _, _, _) => callOrder.Add("ShortLeg"))
            .ReturnsAsync(SuccessOrder());

        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => statusAtAddTime = p.Status);

        await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        // First SaveAsync must come before any leg fires
        callOrder.IndexOf("SaveAsync").Should().BeLessThan(callOrder.IndexOf("LongLeg"));
        callOrder.IndexOf("SaveAsync").Should().BeLessThan(callOrder.IndexOf("ShortLeg"));

        // Capture the status AT THE TIME the position was added — must be Opening
        statusAtAddTime.Should().NotBeNull();
        statusAtAddTime.Should().Be(PositionStatus.Opening);
    }

    [Fact]
    public async Task OpenPosition_LegFail_PositionPersistedAsEmergencyClosed()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long failed"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        // Position must exist in DB (added before legs) and end as EmergencyClosed
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
    }

    [Fact]
    public async Task OpenPosition_LongLegFails_ClosesShortLeg_ReturnsError()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long failed"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Long failed");
        _mockShortConnector.Verify(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_ShortLegFails_ClosesLongLeg_ReturnsError()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short failed"));
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Short failed");
        _mockLongConnector.Verify(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_BothLegsFail_ReturnsError_NoEmergencyClose()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long failed"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short failed"));

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        _mockLongConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockShortConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// C1: If one leg throws an exception (not just returns Success=false), the other
    /// successful leg must be emergency-closed and the position marked EmergencyClosed.
    /// </summary>
    [Fact]
    public async Task OpenPosition_OneLegThrows_EmergencyClosesOtherLeg()
    {
        // Long leg throws (simulating SDK exception from Hyperliquid/Aster)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Short leg succeeds
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        // Emergency close on the short leg must be called
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        _mockShortConnector.Verify(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()), Times.Once);
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.AtLeastOnce);
    }

    /// <summary>
    /// C1: If both legs throw exceptions, the position must be marked EmergencyClosed
    /// and an alert created with both error details.
    /// </summary>
    [Fact]
    public async Task OpenPosition_BothLegsThrow_MarksEmergencyClosed()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Long connection refused"));

        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Short SDK error"));

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // No emergency close attempted since neither leg succeeded
        _mockLongConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockShortConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.AtLeastOnce);
    }

    /// <summary>
    /// Root cause of the leg size mismatch: emergency close returned Success=false
    /// (e.g. "No open position found" on Lighter before on-chain settlement),
    /// but the old code discarded the return value. Now we check it and create a critical alert.
    /// </summary>
    [Fact]
    public async Task OpenPosition_EmergencyCloseReturnsFailure_CreatesCriticalAlert()
    {
        // Short leg succeeds, long leg fails → emergency close fires on short
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long margin insufficient"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        // Emergency close returns failure (position not settled yet / not found)
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("No open position found"));

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();

        // Must create TWO LegFailed alerts: one for the failed emergency close, one for the overall failure
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Once);
    }

    // ── Pre-flight margin check ────────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_InsufficientMarginOnShortExchange_AbortsWithoutOpeningLegs()
    {
        // Short exchange has no balance — pre-flight should catch this
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Insufficient margin on Lighter");

        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockShortConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // No sentinel record should have been created
        _mockPositions.Verify(p => p.Add(It.IsAny<ArbitragePosition>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_InsufficientMarginOnLongExchange_AbortsWithoutOpeningLegs()
    {
        // C2: requiredMargin = sizeUsdc (not sizeUsdc / leverage). Balance=90, size=100 → fail
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(90m);

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Insufficient margin on Hyperliquid");
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_BalanceCheckThrows_AbortsWithoutOpeningLegs()
    {
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Exchange unreachable"));

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Pre-flight balance check failed");
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ClosePositionAsync ─────────────────────────────────────────────────────

    private ArbitragePosition MakeOpenPosition(decimal longEntry = 3000m, decimal shortEntry = 3001m) =>
        new()
        {
            Id = 42,
            UserId = "admin-user-id",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            Leverage = 5,
            LongEntryPrice = longEntry,
            ShortEntryPrice = shortEntry,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0005m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

    [Fact]
    public async Task ClosePosition_CloseBothLegs_UpdatesStatus()
    {
        var position = MakeOpenPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 3010m, 0.1m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-short", 3009m, 0.1m));

        await _sut.ClosePositionAsync(position, CloseReason.SpreadCollapsed, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.Closed);
        _mockPositions.Verify(p => p.Update(position), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePosition_SetsCloseReason_And_ClosedAt()
    {
        var position = MakeOpenPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        await _sut.ClosePositionAsync(position, CloseReason.MaxHoldTimeReached, CancellationToken.None);

        position.CloseReason.Should().Be(CloseReason.MaxHoldTimeReached);
        position.ClosedAt.Should().NotBeNull();
        position.ClosedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClosePosition_CreatesPositionClosedAlert()
    {
        var position = MakeOpenPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        await _sut.ClosePositionAsync(position, CloseReason.Manual, CancellationToken.None);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.PositionClosed)), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_LegFail_CreatesLegFailedAlert()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("API error"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("API error"));

        await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.Once);
    }

    // ── C-EE1: PnL with differing fill quantities ─────────────────────────────

    /// <summary>
    /// C-EE1: When long fills 0.033 and short fills 0.034, PnL must be computed per leg.
    /// longPnl  = (3010 - 3000) * 0.033 = 0.33
    /// shortPnl = (3001 - 2990) * 0.034 = 0.374
    /// total    = 0.33 + 0.374 = 0.704
    /// </summary>
    [Fact]
    public async Task ClosePosition_DifferingFillQuantities_ComputesPnlPerLeg()
    {
        var position = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true, OrderId = "cl", FilledPrice = 3010m, FilledQuantity = 0.033m
            });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true, OrderId = "cs", FilledPrice = 2990m, FilledQuantity = 0.034m
            });

        await _sut.ClosePositionAsync(position, CloseReason.Manual, CancellationToken.None);

        // longPnl  = (3010 - 3000) * 0.033 = 0.33
        // shortPnl = (3001 - 2990) * 0.034 = 0.374
        var expectedPnl = (3010m - 3000m) * 0.033m + (3001m - 2990m) * 0.034m;
        position.RealizedPnl.Should().BeApproximately(expectedPnl, 0.0001m);
    }

    /// <summary>
    /// Sanity check: when both legs fill equal quantities the PnL formula still matches the old formula.
    /// </summary>
    [Fact]
    public async Task ClosePosition_EqualFillQuantities_PnlMatchesSymmetricFormula()
    {
        var position = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        const decimal qty = 0.1m;

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true, OrderId = "cl", FilledPrice = 3010m, FilledQuantity = qty
            });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true, OrderId = "cs", FilledPrice = 2990m, FilledQuantity = qty
            });

        await _sut.ClosePositionAsync(position, CloseReason.Manual, CancellationToken.None);

        var longPnl  = (3010m - 3000m) * qty;
        var shortPnl = (3001m - 2990m) * qty;
        position.RealizedPnl.Should().BeApproximately(longPnl + shortPnl, 0.0001m);
    }

    // ── C2: Close-leg Success=false scenarios (LighterConnector pattern) ──────

    [Fact]
    public async Task ClosePosition_OneLegReturnsSuccessFalse_StaysClosing_NoZeroPnl()
    {
        var position = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 3010m, 0.1m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Order rejected by exchange"));

        await _sut.ClosePositionAsync(position, CloseReason.Manual, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.Closing);
        position.RealizedPnl.Should().BeNull();
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Severity == AlertSeverity.Critical)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePosition_BothLegsReturnSuccessFalse_MarksEmergencyClosed()
    {
        var position = MakeOpenPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long close rejected"));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short close rejected"));

        await _sut.ClosePositionAsync(position, CloseReason.Manual, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.EmergencyClosed);
        position.RealizedPnl.Should().BeNull();
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Severity == AlertSeverity.Critical)),
            Times.AtLeastOnce);
    }

    // ── H4: Entry price preservation ──────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_OneLegFails_EntryPricesPreserved()
    {
        const decimal originalLongEntry = 3000m;
        const decimal originalShortEntry = 3001m;
        var position = MakeOpenPosition(longEntry: originalLongEntry, shortEntry: originalShortEntry);

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 3010m, 0.1m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short close rejected"));

        await _sut.ClosePositionAsync(position, CloseReason.Manual, CancellationToken.None);

        position.LongEntryPrice.Should().Be(originalLongEntry);
        position.ShortEntryPrice.Should().Be(originalShortEntry);
    }

    // ── C1: Close-leg exception scenarios ─────────────────────────────────────

    [Fact]
    public async Task ClosePosition_LongLegThrows_ShortSucceeds_CreatesAlertAndStaysClosing()
    {
        var position = MakeOpenPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Exchange timeout on long leg"));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-short", 3009m, 0.1m));

        await _sut.ClosePositionAsync(position, CloseReason.SpreadCollapsed, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.Closing);
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Severity == AlertSeverity.Critical)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePosition_ShortLegThrows_LongSucceeds_CreatesAlertAndStaysClosing()
    {
        var position = MakeOpenPosition();

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 3010m, 0.1m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Exchange timeout on short leg"));

        await _sut.ClosePositionAsync(position, CloseReason.SpreadCollapsed, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.Closing);
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Severity == AlertSeverity.Critical)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePosition_BothLegsThrow_MarksEmergencyClosed()
    {
        var position = MakeOpenPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Long exchange unreachable"));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Short exchange unreachable"));

        await _sut.ClosePositionAsync(position, CloseReason.SpreadCollapsed, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.EmergencyClosed);
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Severity == AlertSeverity.Critical)),
            Times.AtLeastOnce);
    }

    // ── D4: Emergency close serialization ──────────────────────

    [Fact]
    public async Task OpenPosition_EmergencyClose_RunsSequentially()
    {
        // Long leg returns Success=false (triggers emergency close of successful short leg)
        // Short leg succeeds with a real order
        // Verify: short close completes before SaveAsync is called (sequential, not WhenAll)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long margin insufficient"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var callOrder = new List<string>();

        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ShortEmergencyClose"))
            .ReturnsAsync(SuccessOrder("close-short"));

        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(_ => callOrder.Add("SaveAsync"))
            .ReturnsAsync(1);

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();

        // Verify the short emergency close completes before the final SaveAsync
        var closeIndex = callOrder.IndexOf("ShortEmergencyClose");
        var lastSaveIndex = callOrder.LastIndexOf("SaveAsync");

        closeIndex.Should().BeGreaterThanOrEqualTo(0, "short emergency close must be called");
        lastSaveIndex.Should().BeGreaterThan(closeIndex,
            "emergency close must complete before SaveAsync — verifies sequential not parallel execution");
    }
}
