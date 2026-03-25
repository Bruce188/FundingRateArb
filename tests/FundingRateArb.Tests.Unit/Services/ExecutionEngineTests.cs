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

        // Set up user credentials so CreateForUserAsync returns user-specific connectors
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk"));

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        // Default: both exchanges have ample balance for pre-flight margin check
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);

        _sut = new ExecutionEngine(_mockUow.Object, _mockFactory.Object, _mockUserSettings.Object, NullLogger<ExecutionEngine>.Instance);
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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

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
        // Fill quantity must be >= 95% of expected (100*5/3000.5 ≈ 0.167) to avoid partial fill detection
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 3010m, 0.167m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-short", 3009m, 0.167m));

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.Closed);
        _mockPositions.Verify(p => p.Update(position), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePosition_SetsCloseReason_And_ClosedAt()
    {
        var position = MakeOpenPosition();
        // Fill quantity must be >= 95% of expected to avoid partial fill detection
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("1", 3000m, 0.167m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("2", 3000m, 0.167m));

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.MaxHoldTimeReached, CancellationToken.None);

        position.CloseReason.Should().Be(CloseReason.MaxHoldTimeReached);
        position.ClosedAt.Should().NotBeNull();
        position.ClosedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClosePosition_CreatesPositionClosedAlert()
    {
        var position = MakeOpenPosition();
        // Fill quantity must be >= 95% of expected to avoid partial fill detection
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("1", 3000m, 0.167m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("2", 3000m, 0.167m));

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.Once);
    }

    // ── C-EE1: PnL with differing fill quantities ─────────────────────────────

    /// <summary>
    /// C-EE1: When long and short fill different quantities, PnL must be computed per leg.
    /// longPnl  = (3010 - 3000) * 0.165 = 1.65
    /// shortPnl = (3001 - 2990) * 0.168 = 1.848
    /// pricePnl = 1.65 + 1.848 = 3.498
    /// exitFees = (3010*0.165*0.00045 + 2990*0.168*0) = 0.2232... + 0 = 0.2232
    /// RealizedPnl = pricePnl + AccumulatedFunding(0) - EntryFees(0) - exitFees
    /// </summary>
    [Fact]
    public async Task ClosePosition_DifferingFillQuantities_ComputesPnlPerLeg()
    {
        var position = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "cl",
                FilledPrice = 3010m,
                FilledQuantity = 0.165m
            });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "cs",
                FilledPrice = 2990m,
                FilledQuantity = 0.168m
            });

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // longPnl  = (3010 - 3000) * 0.165 = 1.65
        // shortPnl = (3001 - 2990) * 0.168 = 1.848
        var pricePnl = (3010m - 3000m) * 0.165m + (3001m - 2990m) * 0.168m;
        // exitFees: long=3010*0.165*0.00045(Hyperliquid), short=2990*0.168*0(Lighter)
        var exitFees = 3010m * 0.165m * 0.00045m + 2990m * 0.168m * 0m;
        var expectedPnl = pricePnl + position.AccumulatedFunding - position.EntryFeesUsdc - exitFees;
        position.RealizedPnl.Should().BeApproximately(expectedPnl, 0.001m);
    }

    /// <summary>
    /// Sanity check: when both legs fill equal quantities the PnL formula includes fees and funding.
    /// </summary>
    [Fact]
    public async Task ClosePosition_EqualFillQuantities_PnlMatchesSymmetricFormula()
    {
        var position = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        const decimal qty = 0.167m;

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "cl",
                FilledPrice = 3010m,
                FilledQuantity = qty
            });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "cs",
                FilledPrice = 2990m,
                FilledQuantity = qty
            });

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        var longPnl = (3010m - 3000m) * qty;
        var shortPnl = (3001m - 2990m) * qty;
        var pricePnl = longPnl + shortPnl;
        // exitFees: Hyperliquid long=3010*0.167*0.00045, Lighter short=2990*0.167*0
        var exitFees = 3010m * qty * 0.00045m + 2990m * qty * 0m;
        var expectedPnl = pricePnl + position.AccumulatedFunding - position.EntryFeesUsdc - exitFees;
        position.RealizedPnl.Should().BeApproximately(expectedPnl, 0.001m);
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

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

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

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

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

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

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

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed, CancellationToken.None);

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

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed, CancellationToken.None);

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

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed, CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();

        // Verify the short emergency close completes before the final SaveAsync
        var closeIndex = callOrder.IndexOf("ShortEmergencyClose");
        var lastSaveIndex = callOrder.LastIndexOf("SaveAsync");

        closeIndex.Should().BeGreaterThanOrEqualTo(0, "short emergency close must be called");
        lastSaveIndex.Should().BeGreaterThan(closeIndex,
            "emergency close must complete before SaveAsync — verifies sequential not parallel execution");
    }

    // ── D1: Fee tracking tests ──────────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_BothLegsSucceed_RecordsEntryFees()
    {
        // Hyperliquid (0.00045) long, Lighter (0) short
        // FilledPrice=50000, FilledQuantity=0.1 → notional=5000 each
        // EntryFees = 5000*0.00045 + 5000*0 = 2.25
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "l1", FilledPrice = 50000m, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "s1", FilledPrice = 50000m, FilledQuantity = 0.1m });

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        savedPos.Should().NotBeNull();
        // Hyperliquid fee: 50000*0.1*0.00045 = 2.25, Lighter fee: 50000*0.1*0 = 0
        savedPos!.EntryFeesUsdc.Should().Be(2.25m);
    }

    [Fact]
    public async Task ClosePosition_BothLegsSucceed_RealizedPnlIncludesFundingAndFees()
    {
        var position = MakeOpenPosition(longEntry: 50000m, shortEntry: 50000m);
        position.AccumulatedFunding = 10.0m;
        position.EntryFeesUsdc = 4.0m;
        // Adjust SizeUsdc and Leverage so fills pass partial fill check
        // avgEntry=50000, expectedQty = 100*5/50000 = 0.01
        // Fill qty 0.01 → 100% fill ratio, passes

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "cl", FilledPrice = 51000m, FilledQuantity = 0.01m });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "cs", FilledPrice = 51000m, FilledQuantity = 0.01m });

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // longPnl = (51000-50000)*0.01 = 10, shortPnl = (50000-51000)*0.01 = -10, pricePnl = 0
        // exitFees: Hyperliquid=51000*0.01*0.00045=0.2295, Lighter=51000*0.01*0=0
        var exitFees = 51000m * 0.01m * 0.00045m;
        var expectedPnl = 0m + 10.0m - 4.0m - exitFees;
        position.RealizedPnl.Should().BeApproximately(expectedPnl, 0.001m);
    }

    [Fact]
    public async Task ClosePosition_BothLegsSucceed_RecordsExitFees()
    {
        var position = MakeOpenPosition(longEntry: 50000m, shortEntry: 50000m);
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "cl", FilledPrice = 50000m, FilledQuantity = 0.01m });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "cs", FilledPrice = 50000m, FilledQuantity = 0.01m });

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        position.ExitFeesUsdc.Should().BeGreaterThan(0m);
    }

    // ── D2: Order safety tests ─────────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_ExceedsSafetyCap_Rejected()
    {
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 15000m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("safety cap");
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_FillQuantityMismatch_LogsWarning()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "l1", FilledPrice = 3000m, FilledQuantity = 0.10m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "s1", FilledPrice = 3001m, FilledQuantity = 0.08m });

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        savedPos.Should().NotBeNull();
        savedPos!.Notes.Should().Contain("mismatch");
    }

    [Fact]
    public async Task ClosePosition_PartialFill_StaysInClosingStatus()
    {
        var position = MakeOpenPosition(longEntry: 3000m, shortEntry: 3001m);
        // expectedQty = 100*5/3000.5 ≈ 0.167, fill at 50% → partial
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "cl", FilledPrice = 3010m, FilledQuantity = 0.08m });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "cs", FilledPrice = 2990m, FilledQuantity = 0.08m });

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        position.Status.Should().Be(PositionStatus.Closing);
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Message!.Contains("Partial close"))), Times.Once);
    }

    // ── D9: Emergency close retry tests ─────────────────────────────────────────

    [Fact]
    public async Task EmergencyClose_RetryOnNoOpenPosition_ThenFails()
    {
        // Long succeeds, short fails → triggers emergency close on long leg
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("l1"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("API error"));

        // Emergency close retries all return "No open position found"
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "No open position found" });

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        // Should have tried 3 times (retry on "No open position")
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Exactly(3));

        // Alert created for emergency close failure
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Once);
    }

    [Fact]
    public async Task EmergencyClose_ExceptionDuringRetry_CreatesAlert()
    {
        // Long succeeds, short fails → triggers emergency close on long leg
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("l1"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("API error"));

        // Emergency close throws exception
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        // Alert created for exception during emergency close
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Once);
    }

    // ── Pre-flight leverage validation ───────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_LeverageClamped_UsesReducedLeverage()
    {
        // Long exchange max leverage is 3x (less than configured 5x)
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();
        // Verify orders were placed with clamped leverage (3x, not 5x)
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 3, It.IsAny<CancellationToken>()), Times.Once);
        _mockShortConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_LeverageClamped_CreatesLeverageReducedAlert()
    {
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LeverageReduced &&
                al.Severity == AlertSeverity.Warning &&
                al.Message!.Contains("5x to 3x"))),
            Times.Once);
    }

    [Fact]
    public async Task OpenPosition_NullMaxLeverage_UsesConfiguredLeverage()
    {
        // Both exchanges return null — cannot determine max leverage
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();
        // Configured leverage (5x) should be used since max is unknown
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()), Times.Once);

        // No LeverageReduced alert should have been created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LeverageReduced)),
            Times.Never);
    }

    [Fact]
    public async Task OpenPosition_LeverageCheckThrows_UsesConfiguredLeverage()
    {
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Exchange unreachable"));
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Exchange unreachable"));

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();
        // Falls back to configured leverage (5x) when check fails
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_BothExchangesClampLeverage_BothAlertsShowOriginalLeverage()
    {
        // Configured leverage = 5x, long max = 3x, short max = 2x
        // Both alerts should say "from 5x" (original), not "from 3x" (intermediate)
        _mockLongConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _mockShortConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();

        // Both alerts should reference the original configured leverage (5x)
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LeverageReduced &&
                al.Message!.Contains("from 5x to 3x"))),
            Times.Once);

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LeverageReduced &&
                al.Message!.Contains("from 5x to 2x"))),
            Times.Once);
    }

    // ── ClosePositionAsync — partial failure ─────────────────────────────────

    [Fact]
    public async Task ClosePositionAsync_ShortLegThrows_CreatesAlertAndLeavesInClosing()
    {
        // Arrange: open position with nav properties loaded
        var position = new ArbitragePosition
        {
            Id = 42,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            Leverage = 5,
            MarginUsdc = 20m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0003m,
            AccumulatedFunding = 0.5m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            UserId = "test-user",
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };

        // Long leg closes successfully
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long-1", 3100m, 0.1m));

        // Short leg throws an exception
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Exchange API unavailable"));

        // Act — should not throw
        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.SpreadCollapsed, CancellationToken.None);

        // Assert: position stays in Closing (not Closed, not EmergencyClosed)
        position.Status.Should().Be(PositionStatus.Closing,
            "partial failure should leave position in Closing for manual intervention");

        // Assert: a LegFailed alert was created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("short") &&
                al.ArbitragePositionId == 42)),
            Times.Once);

        // Assert: SaveAsync was called (at least for the alert + position update)
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task ClosePositionAsync_ShortLegReturnsFailed_CreatesAlertForPartialFailure()
    {
        // Arrange: test the non-throwing failure path (Success=false instead of exception)
        var position = new ArbitragePosition
        {
            Id = 43,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            Leverage = 5,
            MarginUsdc = 20m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0003m,
            AccumulatedFunding = 0m,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            UserId = "test-user",
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };

        // Long leg succeeds
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long-2", 3100m, 0.1m));

        // Short leg returns failure (no exception)
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Insufficient margin for close order"));

        // Act
        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // Assert: alert created for partial close failure
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.ArbitragePositionId == 43)),
            Times.Once);

        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    // ── B2/B3: Credential failure path tests ─────────────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_MissingLongCredentials_ReturnsError()
    {
        // Only short credential exists — long exchange has no credentials
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(TestUserId))
            .ReturnsAsync(new List<UserExchangeCredential> { shortCred });

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Hyperliquid");
        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // N6: SaveAsync must NOT be called on credential failure
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPositionAsync_MissingShortCredentials_ReturnsError()
    {
        // Only long credential exists — short exchange has no credentials
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(TestUserId))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred });

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Lighter");
        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // N6: SaveAsync must NOT be called on credential failure
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPositionAsync_FactoryReturnsNullConnector_ReturnsError()
    {
        // Credentials exist but factory returns null for long exchange
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((IExchangeConnector?)null);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Hyperliquid");
        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClosePositionAsync_MissingCredentials_CreatesCriticalAlertAndPreservesStatus()
    {
        var position = MakeOpenPosition();
        // Return empty credential list — no credentials for any exchange
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(TestUserId))
            .ReturnsAsync(new List<UserExchangeCredential>());

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // Position status must NOT change — remains Open for manual intervention
        position.Status.Should().Be(PositionStatus.Open);

        // A critical alert must be created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("Manual intervention required"))),
            Times.Once);

        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClosePositionAsync_FactoryReturnsNullConnector_CreatesCriticalAlertAndPreservesStatus()
    {
        var position = MakeOpenPosition();
        // Credentials exist but factory returns null for long exchange
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((IExchangeConnector?)null);

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // Position status must NOT change — remains Open for manual intervention
        position.Status.Should().Be(PositionStatus.Open);

        // A critical alert must be created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("Manual intervention required"))),
            Times.Once);

        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── NB4: DecryptCredential exception path ──────────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_DecryptCredentialThrows_ReturnsError()
    {
        // DecryptCredential throws CryptographicException for the long exchange
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 1)))
            .Throws(new System.Security.Cryptography.CryptographicException("Corrupt key data"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Credential validation failed");
        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClosePositionAsync_DecryptCredentialThrows_CreatesCriticalAlertAndPreservesStatus()
    {
        var position = MakeOpenPosition();
        // DecryptCredential throws for the long exchange
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 1)))
            .Throws(new System.Security.Cryptography.CryptographicException("Corrupt key data"));

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // Position status must NOT change — remains Open for manual intervention
        position.Status.Should().Be(PositionStatus.Open);

        // A critical alert must be created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("Manual intervention required"))),
            Times.Once);

        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── B2: Null/empty userId tests ──────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OpenPositionAsync_NullOrEmptyUserId_ReturnsError(string? userId)
    {
        var result = await _sut.OpenPositionAsync(userId!, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("User ID is required");
        // No exchange calls should have been made
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockFactory.Verify(f => f.CreateForUserAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ClosePositionAsync_NullOrEmptyUserId_CreatesAlertAndReturns(string? userId)
    {
        var position = MakeOpenPosition();

        await _sut.ClosePositionAsync(userId!, position, CloseReason.Manual, CancellationToken.None);

        // Position status must NOT change — remains Open for manual intervention
        position.Status.Should().Be(PositionStatus.Open);

        // A critical alert must be created
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("Manual intervention required"))),
            Times.Once);

        // No exchange calls should have been made
        _mockLongConnector.Verify(c => c.ClosePositionAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── NB5: Connector disposal tests ──────────────────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_Success_DisposesConnectorsAfterCompletion()
    {
        var mockDisposableLong = new Mock<IExchangeConnector>();
        mockDisposableLong.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var mockDisposableShort = new Mock<IExchangeConnector>();
        mockDisposableShort.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableShort.Object);

        mockDisposableLong
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        mockDisposableShort
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        mockDisposableLong
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        mockDisposableShort
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();
        mockDisposableLong.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
        mockDisposableShort.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task OpenPositionAsync_Failure_DisposesConnectorsAfterCompletion()
    {
        var mockDisposableLong = new Mock<IExchangeConnector>();
        mockDisposableLong.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var mockDisposableShort = new Mock<IExchangeConnector>();
        mockDisposableShort.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableShort.Object);

        mockDisposableLong
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        mockDisposableShort
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        mockDisposableLong
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long failed"));
        mockDisposableShort
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short failed"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        mockDisposableLong.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
        mockDisposableShort.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
    }

    // ── NB6: CreateForUserAsync exception path ──────────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_CreateForUserAsyncThrows_ReturnsError()
    {
        // CreateForUserAsync throws (not returns null) for long exchange
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("Exchange SDK initialization failed"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Exchange connection failed");
        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── NB7: Case-insensitive credential matching ───────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_DifferentCaseExchangeNames_MatchesCredentials()
    {
        // Credentials stored with different casing than opportunity exchange names
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "LIGHTER" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(TestUserId))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });

        // Factory should still be called with the opportunity's casing
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, CancellationToken.None);

        result.Success.Should().BeTrue();
        // Verify connectors were created (credentials matched despite different casing)
        _mockFactory.Verify(f => f.CreateForUserAsync("Hyperliquid",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _mockFactory.Verify(f => f.CreateForUserAsync("Lighter",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }
}
