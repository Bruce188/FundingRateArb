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
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));
        // Default: user leverage matches bot config so existing tests pass unchanged
        _mockUserSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5 });

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        // Position must exist in DB (added before legs) and end as EmergencyClosed
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
    }

    [Fact]
    public async Task OpenPosition_FirstLegFails_AbortsWithoutOpeningSecondLeg()
    {
        // Sequential path: short is estimated-fill, so it opens first; if it fails,
        // the reliable leg (long) is never opened.
        _mockShortConnector
            .Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short failed"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Short failed");
        // Second leg (long) should never be called
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // No emergency close needed — first leg returned failure, nothing to close
        _mockShortConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLongConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        _mockLongConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockShortConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_EmergencyClosedAtOpen_SetsFeesAndNegativePnl()
    {
        // Long leg succeeds with fill (price=3000, qty=0.1), short fails → emergency close
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m, 0.1m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short failed"));
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        savedPos.Should().NotBeNull();
        savedPos!.EntryFeesUsdc.Should().BeGreaterThan(0, "should record entry fees from the one leg that opened");
        savedPos.ExitFeesUsdc.Should().BeGreaterThan(0, "should record exit fees from the emergency close");
        savedPos.RealizedPnl.Should().BeNegative("emergency close incurs fees with no funding collected");
    }

    /// <summary>
    /// C1: If the first leg throws an exception (sequential path), the position is marked
    /// EmergencyClosed and the second leg is never opened.
    /// </summary>
    [Fact]
    public async Task OpenPosition_FirstLegThrows_AbortsWithoutSecondLeg()
    {
        // Sequential path: short is estimated-fill, so it opens first
        _mockShortConnector
            .Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // Second leg (long) should never be called
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.AtLeastOnce);
    }

    /// <summary>
    /// C1: If the second leg throws an exception, the first leg must be emergency-closed
    /// and the position marked EmergencyClosed.
    /// </summary>
    [Fact]
    public async Task OpenPosition_SecondLegThrows_EmergencyClosesFirstLeg()
    {
        // Long leg succeeds (first with default mock config)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));

        // Short leg throws (second leg)
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Emergency close on the long leg must be called
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        _mockLongConnector.Verify(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()), Times.Once);
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.AtLeastOnce);
    }

    /// <summary>
    /// C1: If the first leg throws (sequential path), the position must be marked
    /// EmergencyClosed and the second leg is never attempted.
    /// </summary>
    [Fact]
    public async Task OpenPosition_FirstLegThrows_MarksEmergencyClosed_NoSecondLeg()
    {
        // Sequential path: short is estimated-fill, so it opens first
        _mockShortConnector
            .Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Short connection refused"));

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        addedPosition.ClosedAt.Should().NotBeNull("EmergencyClosed positions must have ClosedAt set");
        // No emergency close on first leg (it threw) and second leg never attempted
        _mockShortConnector.Verify(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.AtLeastOnce);
    }

    /// <summary>
    /// When emergency close returns "No open position found", it exits silently —
    /// no critical alert needed because the position never existed on the exchange.
    /// </summary>
    [Fact]
    public async Task OpenPosition_EmergencyCloseNoPositionFound_NoCriticalAlert()
    {
        // Long leg succeeds (first leg), short leg fails → emergency close fires on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short margin insufficient"));

        // Emergency close returns "no open position" — position never existed
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("No open position found"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();

        // No critical "EMERGENCY CLOSE FAILED" alert — position never existed
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Never);
    }

    /// <summary>
    /// When emergency close returns a non-position error (e.g. "Insufficient margin"),
    /// a critical alert must be created.
    /// </summary>
    [Fact]
    public async Task OpenPosition_EmergencyCloseReturnsFailure_CreatesCriticalAlert()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short margin insufficient"));

        // Emergency close returns non-position error
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Insufficient margin for close order"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
        // requiredMargin = sizeUsdc = 100. Balance=15 < 100 → fail
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(15m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Insufficient margin on Hyperliquid");
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_BalanceBetweenLeveragedMarginAndFullMargin_BlocksTrade()
    {
        // sizeUsdc=100, leverage=5. Old (buggy): requiredMargin=100/5=20, would pass.
        // Fixed: requiredMargin=100, balance=50 < 100 → blocked.
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Pre-flight balance check failed");
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── User leverage ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_UsesUserLeverageInsteadOfBotConfig()
    {
        // User has leverage=1, bot config has leverage=5
        _mockUserSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 1 });

        // With leverage=1, PlaceMarketOrderAsync should receive leverage=1
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        // Verify the orders were placed with leverage=1, not leverage=5
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 1, It.IsAny<CancellationToken>()), Times.Once);
        _mockShortConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_FallsBackToBotConfigLeverage_WhenUserLeverageIsZero()
    {
        // User has leverage=0 (not set), bot config has leverage=5
        _mockUserSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 0 });

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_DefensiveFloor_ClampsLeverageTo1_WhenBothConfigsAreZero()
    {
        // Both user and bot config have leverage=0 (corrupted DB)
        _mockUserSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 0 });
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration
            {
                DefaultLeverage = 0,
                MaxCapitalPerPosition = DefaultConfig.MaxCapitalPerPosition,
                TotalCapitalUsdc = DefaultConfig.TotalCapitalUsdc,
                MinPositionSizeUsdc = DefaultConfig.MinPositionSizeUsdc,
                BreakevenHoursMax = DefaultConfig.BreakevenHoursMax,
                VolumeFraction = DefaultConfig.VolumeFraction,
                MaxExposurePerAsset = DefaultConfig.MaxExposurePerAsset,
                MaxExposurePerExchange = DefaultConfig.MaxExposurePerExchange,
            });

        // Leverage floor should clamp to 1 — PlaceMarketOrderAsync receives leverage=1
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 1, It.IsAny<CancellationToken>()), Times.Once);
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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
        position.ClosedAt.Should().NotBeNull("EmergencyClosed positions must have ClosedAt set");
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
    public async Task OpenPosition_SecondLegFails_EmergencyClosesFirstLeg_BeforeSave()
    {
        // Long leg succeeds (first leg), short leg fails (second leg)
        // Verify: long emergency close completes before final SaveAsync
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short margin insufficient"));

        var callOrder = new List<string>();

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("LongEmergencyClose"))
            .ReturnsAsync(SuccessOrder("close-long"));

        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(_ => callOrder.Add("SaveAsync"))
            .ReturnsAsync(1);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();

        // Verify the long emergency close completes before the final SaveAsync
        var closeIndex = callOrder.IndexOf("LongEmergencyClose");
        var lastSaveIndex = callOrder.LastIndexOf("SaveAsync");

        closeIndex.Should().BeGreaterThanOrEqualTo(0, "long emergency close must be called");
        lastSaveIndex.Should().BeGreaterThan(closeIndex,
            "emergency close must complete before SaveAsync — verifies sequential execution");
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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 15000m, ct: CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
    public async Task EmergencyClose_NoOpenPosition_ExitsImmediately()
    {
        // Long succeeds, short fails → triggers emergency close on long leg
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("l1"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("API error"));

        // Emergency close returns "No open position found" — position never existed
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "No open position found" });

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Should exit after 1 attempt — no retries for "no position" errors
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once);

        // No critical alert — position never existed
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical &&
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Never);
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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
        // Credentials exist but factory returns null for long exchange.
        // Set up both exchanges explicitly so test doesn't depend on execution order.
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((IExchangeConnector?)null);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Hyperliquid");
        // Verify the long connector factory was called
        _mockFactory.Verify(f => f.CreateForUserAsync("Hyperliquid",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
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
        // Credentials exist but factory returns null for long exchange.
        // Set up both exchanges explicitly so test doesn't depend on execution order.
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((IExchangeConnector?)null);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // Position status must NOT change — remains Open for manual intervention
        position.Status.Should().Be(PositionStatus.Open);

        // Verify the long connector factory was called
        _mockFactory.Verify(f => f.CreateForUserAsync("Hyperliquid",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);

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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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

    // ── NB3: Short credential decryption failure disposes long connector ──────────

    [Fact]
    public async Task OpenPositionAsync_ShortDecryptCredentialThrows_DisposesLongConnector()
    {
        // Long credential decrypts successfully, short credential throws
        var mockDisposableLong = new Mock<IExchangeConnector>();
        mockDisposableLong.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);

        // DecryptCredential succeeds for long (ExchangeId=1) but throws for short (ExchangeId=2)
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 1)))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 2)))
            .Throws(new System.Security.Cryptography.CryptographicException("Corrupt short key"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Credential validation failed");
        // Verify long connector was disposed after short decryption failure
        mockDisposableLong.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
    }

    // ── B2: Null/empty userId tests ──────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OpenPositionAsync_NullOrEmptyUserId_ReturnsError(string? userId)
    {
        var result = await _sut.OpenPositionAsync(userId!, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("User ID is required");
        // No exchange calls should have been made
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockFactory.Verify(f => f.CreateForUserAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
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
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

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
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("Exchange SDK initialization failed"));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Exchange connection failed");
        // No orders should have been placed
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Sequential leg ordering: estimated fill leg opens first ──────────────────

    [Fact]
    public async Task OpenPosition_EstimatedFillLeg_OpensFirst()
    {
        // Configure short connector as estimated fill (like Lighter)
        _mockShortConnector
            .Setup(c => c.IsEstimatedFillExchange).Returns(true);

        var callOrder = new List<string>();

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, CancellationToken>((_, _, _, _, _) => callOrder.Add("Long"))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync(It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, CancellationToken>((_, _, _, _, _) => callOrder.Add("Short"))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        callOrder.Should().ContainInOrder(new[] { "Short", "Long" },
            "estimated fill leg (short/Lighter) should open before reliable leg (long/Hyperliquid)");
    }

    [Fact]
    public async Task OpenPosition_EstimatedFillVerificationFails_AbortsWithoutSecondLeg()
    {
        // Configure short connector as estimated fill + verifiable
        var mockVerifiableShort = new Mock<IExchangeConnector>();
        mockVerifiableShort.As<IPositionVerifiable>();
        mockVerifiableShort.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        mockVerifiableShort.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mockVerifiableShort.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });
        mockVerifiableShort.As<IPositionVerifiable>()
            .Setup(v => v.VerifyPositionOpenedAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockVerifiableShort.Object);

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("verification failed");
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // Long leg (second leg) should never be called
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_SecondLegFails_EmergencyClosesFirstLeg()
    {
        // Long opens first (default: both IsEstimatedFillExchange=false, so firstIsLong=true)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short exchange error"));
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Short exchange error");
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // First leg (long) must be emergency-closed
        _mockLongConnector.Verify(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenPosition_SecondLegThrows_EmergencyCloseNeverExisted_NoFeesSet()
    {
        // Sequential path: short is estimated fill, opens first, then long (second leg) throws
        _mockShortConnector
            .Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m, 0.1m));
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Emergency close on first leg (short) returns "no open position" — auto-liquidation
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("No open position found for 'ETH' on Lighter"));

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // Position never existed — fees should NOT be set
        addedPosition.EntryFeesUsdc.Should().Be(0, "no fees when position never existed on exchange");
        addedPosition.RealizedPnl.Should().BeNull("no PnL when position never existed");
    }

    [Fact]
    public async Task OpenPosition_SecondLegFails_EmergencyCloseNeverExisted_NoFeesSet()
    {
        // Sequential path: short is estimated fill, opens first, then long (second leg) fails
        _mockShortConnector
            .Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m, 0.1m));
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long exchange error"));

        // Emergency close on first leg (short) returns "no open position" — auto-liquidation
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("No open position found for 'ETH' on Lighter"));

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // Position never existed — fees should NOT be set
        addedPosition.EntryFeesUsdc.Should().Be(0, "no fees when position never existed on exchange");
        addedPosition.RealizedPnl.Should().BeNull("no PnL when position never existed");
    }

    // ── B1: Restore concurrent path for reliable pairs ──────────────────────────

    [Fact]
    public async Task OpenPosition_NeitherEstimatedFill_OpensBothLegsConcurrently()
    {
        // Both connectors are reliable (IsEstimatedFillExchange = false)
        // They should execute concurrently via Task.WhenAll
        _mockLongConnector.Setup(c => c.IsEstimatedFillExchange).Returns(false);
        _mockShortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(false);

        var callTimestamps = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
        var tcs = new TaskCompletionSource();

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .Returns<string, Side, decimal, int, CancellationToken>(async (_, _, _, _, _) =>
            {
                callTimestamps["Long"] = DateTime.UtcNow;
                await tcs.Task; // Block until short also starts
                return SuccessOrder("long-1", 3000m);
            });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .Returns<string, Side, decimal, int, CancellationToken>(async (_, _, _, _, _) =>
            {
                callTimestamps["Short"] = DateTime.UtcNow;
                tcs.TrySetResult(); // Unblock long
                await Task.Yield();
                return SuccessOrder("short-1", 3001m);
            });

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        // Both legs must have been called
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            "ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()), Times.Once);
        _mockShortConnector.Verify(c => c.PlaceMarketOrderAsync(
            "ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── B2: Test verification success path ────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_EstimatedFillVerificationSucceeds_OpensSecondLeg()
    {
        // Configure short connector as estimated fill + verifiable, verification succeeds
        var mockVerifiableShort = new Mock<IExchangeConnector>();
        mockVerifiableShort.As<IPositionVerifiable>();
        mockVerifiableShort.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        mockVerifiableShort.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mockVerifiableShort.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });
        mockVerifiableShort.As<IPositionVerifiable>()
            .Setup(v => v.VerifyPositionOpenedAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockVerifiableShort.Object);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.Open);
        // Second leg (long) must have been called
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            "ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── B3: Emergency close on verification failure ───────────────────────────────

    [Fact]
    public async Task OpenPosition_EstimatedFillVerificationFails_EmergencyClosesFirstLeg()
    {
        // Configure short connector as estimated fill + verifiable, verification fails
        var mockVerifiableShort = new Mock<IExchangeConnector>();
        mockVerifiableShort.As<IPositionVerifiable>();
        mockVerifiableShort.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        mockVerifiableShort.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mockVerifiableShort.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });
        mockVerifiableShort.As<IPositionVerifiable>()
            .Setup(v => v.VerifyPositionOpenedAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // Set up ClosePositionAsync so emergency close can be verified
        mockVerifiableShort.Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockVerifiableShort.Object);

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("verification failed");
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // First leg (short/Lighter) must have emergency close attempted
        mockVerifiableShort.Verify(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Second leg (long) should never be called
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenPosition_VerificationFails_EmergencyCloseSucceeds_SetsFeesAndPnl()
    {
        // Use an opportunity where Hyperliquid (has fees) is estimated-fill, to verify fee recording
        var opp = new ArbitrageOpportunityDto
        {
            AssetSymbol = "ETH",
            AssetId = 1,
            LongExchangeName = "Aster",
            LongExchangeId = 3,
            ShortExchangeName = "Hyperliquid",
            ShortExchangeId = 1,
            SpreadPerHour = 0.0005m,
            NetYieldPerHour = 0.0004m,
            LongMarkPrice = 3000m,
            ShortMarkPrice = 3001m,
        };

        // Short connector (Hyperliquid) = estimated fill + verifiable, verification fails
        var mockVerifiableShort = new Mock<IExchangeConnector>();
        mockVerifiableShort.As<IPositionVerifiable>();
        mockVerifiableShort.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        mockVerifiableShort.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mockVerifiableShort.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });
        mockVerifiableShort.As<IPositionVerifiable>()
            .Setup(v => v.VerifyPositionOpenedAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // Emergency close succeeds — position existed and was closed
        mockVerifiableShort.Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        // Long connector (Aster) — not estimated fill
        var mockLong = new Mock<IExchangeConnector>();
        mockLong.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockVerifiableShort.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Aster", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockLong.Object);

        // Set up credentials for the new exchanges
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<UserExchangeCredential>
            {
                new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } },
                new() { Id = 3, ExchangeId = 3, Exchange = new Exchange { Name = "Aster" } },
            });

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, opp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // Fees should be recorded since position existed and was closed
        // Hyperliquid has 0.045% taker fee: notional = 3001 * 0.1 = 300.1, fee = 300.1 * 0.00045 ≈ 0.135
        addedPosition.EntryFeesUsdc.Should().BeGreaterThan(0, "entry fees should be recorded for the closed leg");
        addedPosition.ExitFeesUsdc.Should().BeGreaterThan(0, "exit fees should be recorded for the closed leg");
        addedPosition.RealizedPnl.Should().NotBeNull("PnL should be calculated for the emergency-closed position");
        addedPosition.RealizedPnl.Should().BeLessThan(0, "PnL should be negative (fees lost)");
    }

    [Fact]
    public async Task OpenPosition_VerificationFails_NoPosition_NoFees()
    {
        // Verification fails and emergency close returns "no open position" (position never existed)
        var mockVerifiableShort = new Mock<IExchangeConnector>();
        mockVerifiableShort.As<IPositionVerifiable>();
        mockVerifiableShort.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        mockVerifiableShort.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        mockVerifiableShort.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "short-1", FilledPrice = 3001m, FilledQuantity = 0.1m, IsEstimatedFill = true });
        mockVerifiableShort.As<IPositionVerifiable>()
            .Setup(v => v.VerifyPositionOpenedAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // Emergency close returns "no open position" (position never existed)
        mockVerifiableShort.Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("No open position found for 'ETH' on Lighter DEX"));

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockVerifiableShort.Object);

        ArbitragePosition? addedPosition = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => addedPosition = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        addedPosition.Should().NotBeNull();
        addedPosition!.Status.Should().Be(PositionStatus.EmergencyClosed);
        // No fees should be recorded since position never existed
        addedPosition.EntryFeesUsdc.Should().Be(0, "no fees when position never existed");
        addedPosition.ExitFeesUsdc.Should().Be(0, "no fees when position never existed");
        addedPosition.RealizedPnl.Should().BeNull("PnL should be null when position never existed");
    }

    // ── NB2: Both-estimated-fill guard ────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_BothEstimatedFill_StillSucceeds()
    {
        // Both connectors are estimated-fill — unusual but should still work
        _mockLongConnector.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(true);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
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

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        // Verify connectors were created (credentials matched despite different casing)
        _mockFactory.Verify(f => f.CreateForUserAsync("Hyperliquid",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _mockFactory.Verify(f => f.CreateForUserAsync("Lighter",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    // ── B2: CoinGlass guard in CreateForUserAsync ─────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_CoinGlassExchange_ReturnsError()
    {
        // CoinGlass is a read-only data source; CreateForUserAsync should throw NotSupportedException
        var opp = new ArbitrageOpportunityDto
        {
            AssetSymbol = "ETH",
            AssetId = 1,
            LongExchangeName = "CoinGlass",
            LongExchangeId = 4,
            ShortExchangeName = "Lighter",
            ShortExchangeId = 2,
            SpreadPerHour = 0.0005m,
            NetYieldPerHour = 0.0004m,
            LongMarkPrice = 3000m,
            ShortMarkPrice = 3001m,
        };

        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 4, Exchange = new Exchange { Name = "CoinGlass" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(TestUserId))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });

        _mockFactory
            .Setup(f => f.CreateForUserAsync("CoinGlass", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new NotSupportedException("CoinGlass is a read-only data source and cannot be used for trading"));

        var result = await _sut.OpenPositionAsync(TestUserId, opp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Exchange connection failed");
    }

    // ── NB5: Null Exchange navigation property on credential ─────────────────

    [Fact]
    public async Task OpenPositionAsync_CredentialWithNullExchangeNavProperty_ReturnsError()
    {
        // Credential exists but Exchange navigation property is null (not loaded via Include)
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = null! };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(TestUserId))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Hyperliquid");
        _mockLongConnector.Verify(c => c.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── N7: ClosePositionAsync disposal tests ────────────────────────────────

    [Fact]
    public async Task ClosePositionAsync_Success_DisposesConnectorsAfterCompletion()
    {
        var mockDisposableLong = new Mock<IExchangeConnector>();
        mockDisposableLong.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var mockDisposableShort = new Mock<IExchangeConnector>();
        mockDisposableShort.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableShort.Object);

        mockDisposableLong
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long-1", 3000m));
        mockDisposableShort
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-short-1", 3001m));

        var position = new ArbitragePosition
        {
            Id = 50,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            SizeUsdc = 100m,
            Leverage = 5,
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        mockDisposableLong.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
        mockDisposableShort.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task ClosePositionAsync_Failure_DisposesConnectorsAfterCompletion()
    {
        var mockDisposableLong = new Mock<IExchangeConnector>();
        mockDisposableLong.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var mockDisposableShort = new Mock<IExchangeConnector>();
        mockDisposableShort.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockDisposableShort.Object);

        mockDisposableLong
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Close failed on long"));
        mockDisposableShort
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Close failed on short"));

        var position = new ArbitragePosition
        {
            Id = 51,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            SizeUsdc = 100m,
            Leverage = 5,
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
        };

        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        mockDisposableLong.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
        mockDisposableShort.As<IAsyncDisposable>().Verify(d => d.DisposeAsync(), Times.Once);
    }

    // ── DEX credential pipeline: SubAccountAddress + ApiKeyIndex ────

    [Fact]
    public async Task CreateUserConnectors_PassesSubAccountAndApiKeyIndex()
    {
        // Arrange — DecryptCredential returns all 6 fields
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 1)))
            .Returns(("key", "secret", "wallet", "pk", "0xSubAccount", (string?)null));
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 2)))
            .Returns(("key2", "secret2", "wallet2", "pk2", (string?)null, "42"));

        // Set up PlaceMarketOrderAsync so test doesn't NullRef after connector creation
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert — verify factory received the subAccountAddress and apiKeyIndex
        _mockFactory.Verify(f => f.CreateForUserAsync(
            "Hyperliquid",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            "0xSubAccount", It.IsAny<string?>()), Times.Once);
        _mockFactory.Verify(f => f.CreateForUserAsync(
            "Lighter",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), "42"), Times.Once);
    }

    // ── Emergency close retry pattern matching ─────────────────────────────

    [Theory]
    [InlineData("Request timeout")]
    [InlineData("rate limit exceeded")]
    [InlineData("HTTP 503")]
    [InlineData("connection reset by peer")]
    public async Task EmergencyClose_RetriesOnRetryableError(string errorMessage)
    {
        // Arrange: long succeeds, short fails → triggers emergency close on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short leg failed"));

        // Emergency close: first attempt returns retryable error, second attempt succeeds
        var callCount = 0;
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new OrderResultDto { Success = false, Error = errorMessage };
                }

                return new OrderResultDto { Success = true, OrderId = "close-1", FilledPrice = 3000m, FilledQuantity = 0.1m };
            });

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: emergency close was called at least 2 times (retry occurred)
        result.Success.Should().BeFalse();
        callCount.Should().BeGreaterThanOrEqualTo(2, "should retry on retryable close error");
    }

    [Fact]
    public async Task EmergencyClose_DoesNotRetryOnUnrelatedError()
    {
        // Arrange: long succeeds, short fails → triggers emergency close on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short leg failed"));

        // Emergency close returns non-retryable error
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "Insufficient margin" });

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: emergency close was called exactly once (no retry for non-retryable error)
        result.Success.Should().BeFalse();
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once,
            "should NOT retry on unrelated error like 'Insufficient margin'");
    }

    [Fact]
    public async Task EmergencyClose_NullError_DoesNotRetry()
    {
        // Arrange: long succeeds, short fails → triggers emergency close on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short leg failed"));

        // Emergency close returns failure with null error
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = null });

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: emergency close was called exactly once (null error is not retryable)
        result.Success.Should().BeFalse();
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once,
            "should NOT retry when close error is null");
    }

    [Theory]
    [InlineData("No open position found for 'STABLE' on Lighter DEX")]
    [InlineData("Position not found")]
    [InlineData("Order does not exist")]
    [InlineData("no position available")]
    public async Task EmergencyClose_NoPositionFound_ExitsAfterOneAttempt(string errorMessage)
    {
        // Arrange: long succeeds, short fails → triggers emergency close on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short leg failed"));

        // Emergency close returns "no position" error
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = errorMessage });

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: only 1 attempt — no retries for "no position" errors
        result.Success.Should().BeFalse();
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once,
            "should exit immediately on 'no position found' without retrying");
    }

    [Fact]
    public async Task EmergencyClose_RetriesExceptionsThenCreatesAlert()
    {
        // Arrange: long succeeds, short fails → triggers emergency close on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short leg failed"));

        // Emergency close throws network exception every time
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection reset"));

        // Act
        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: should retry all 5 attempts (exceptions are now retryable)
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Exactly(5),
            "exceptions should be retried up to maxAttempts");

        // Alert created only on final attempt
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Once);
    }

    // ── TruncateError Tests ──────────────────────────────────────────────────────

    [Fact]
    public void TruncateError_NullInput_ReturnsEmptyString()
    {
        var result = ExecutionEngine.TruncateError(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void TruncateError_ShortInput_ReturnsUnchanged()
    {
        var shortError = "Insufficient margin";

        var result = ExecutionEngine.TruncateError(shortError);

        result.Should().Be(shortError);
    }

    [Fact]
    public void TruncateError_LongInput_TruncatesWithEllipsis()
    {
        var longError = new string('x', 2500);

        var result = ExecutionEngine.TruncateError(longError);

        result.Length.Should().BeLessOrEqualTo(1901); // 1900 + 1 for ellipsis
        result.Should().EndWith("…");
    }

    [Fact]
    public void TruncateError_ExactBoundary_ReturnsUnchanged()
    {
        var exactError = new string('y', 1900);

        var result = ExecutionEngine.TruncateError(exactError);

        result.Should().Be(exactError);
        result.Should().NotEndWith("…");
    }

    [Fact]
    public void TruncateError_CustomMaxLength_TruncatesCorrectly()
    {
        var longError = new string('z', 2000);

        var result = ExecutionEngine.TruncateError(longError, 900);

        result.Length.Should().BeLessOrEqualTo(901); // 900 + 1 for ellipsis
        result.Should().EndWith("…");
    }

    [Fact]
    public async Task DualErrorAlert_BothLongErrors_StaysWithinColumnLimit()
    {
        // Arrange: set up a close scenario where both legs fail with long error messages
        // This tests the "Close failed on BOTH legs" alert path (lines 519-521)
        var longError = new string('A', 2500);
        var shortError = new string('B', 2500);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m));

        // Open position first
        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Set up close to fail on both legs with long errors
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception(longError));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception(shortError));

        Alert? capturedAlert = null;
        _mockAlerts.Setup(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)))
            .Callback<Alert>(a => capturedAlert = a);

        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Lighter" },
            Asset = new Asset { Symbol = "ETH" },
            Status = PositionStatus.Open,
        };

        // Act
        await _sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual, CancellationToken.None);

        // Assert: the alert message must fit in the 2000-char column
        capturedAlert.Should().NotBeNull("a LegFailed alert should have been created");
        capturedAlert!.Message.Length.Should().BeLessOrEqualTo(2000,
            "dual-error alert message must not exceed the nvarchar(2000) column limit");
    }

    // ── N2: Emergency close exception retry succeeds on second attempt ──────────

    [Fact]
    public async Task EmergencyClose_ExceptionThenSuccess_ExactlyTwoInvocations_NoCriticalAlert()
    {
        // Arrange: long succeeds, short fails → triggers emergency close on long
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short leg failed"));

        // Emergency close: first attempt throws exception, second attempt succeeds
        var callCount = 0;
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("Connection reset");
                }

                return new OrderResultDto { Success = true, OrderId = "close-1", FilledPrice = 3000m, FilledQuantity = 0.1m };
            });

        // Act
        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: exactly 2 invocations (throw + success)
        callCount.Should().Be(2, "should retry after exception and succeed on second attempt");

        // No critical EMERGENCY CLOSE FAILED alert since second attempt succeeded
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Message!.Contains("EMERGENCY CLOSE FAILED"))),
            Times.Never,
            "should not create critical alert when retry succeeds");
    }

    // ── NB4: Order timeout — 45-second CTS on PlaceMarketOrderAsync ────────────

    [Fact]
    public async Task OpenPosition_OrderTimeout_FailsWithoutHanging()
    {
        // Use sequential path: mark short connector as estimated-fill
        _mockShortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(true);

        // First leg (long, since short is estimated → long opens first via sequential logic:
        // firstIsLong = false || !true = false → short goes first)
        // Actually: firstIsLong = longConnector.IsEstimatedFillExchange || !shortConnector.IsEstimatedFillExchange
        //         = false || !true = false → firstIsLong = false → short connector goes first
        // So short goes first. Make it never complete — cancelled by the 45s linked CTS.
        var tcs = new TaskCompletionSource<OrderResultDto>();
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .Returns((string _, Side _, decimal _, int _, CancellationToken ct) =>
            {
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            });

        // Act: the 45s linked CTS fires, the catch(Exception) block handles it, returns (false, message)
        var task = _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: operation completes within 50s (the 45s CTS internally terminates it)
        var watchdog = Task.Delay(TimeSpan.FromSeconds(50));
        var completed = await Task.WhenAny(task, watchdog);
        completed.Should().BeSameAs(task, "operation should complete via internal timeout, not hang indefinitely");

        // Await the result — sequential path catches the exception and returns (false, message)
        var result = await task;
        result.Success.Should().BeFalse("should fail when order times out");
    }
}
