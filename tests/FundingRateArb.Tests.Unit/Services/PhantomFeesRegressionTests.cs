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

/// <summary>
/// Regression tests for the phantom-fees bug: 38 EmergencyClosed positions in production
/// have LongFilledQuantity=0 AND ShortFilledQuantity=0 but non-zero EntryFeesUsdc + ExitFeesUsdc.
///
/// Root cause: in all emergency-close paths the code calls SetEmergencyCloseFees (which records
/// fees derived from the OrderResultDto) but never writes the OrderResultDto's FilledQuantity
/// back to position.LongFilledQuantity / position.ShortFilledQuantity.  The fields are only
/// assigned at the "both legs succeeded" code-path (ExecutionEngine.cs lines 697-702), so every
/// failure-branch exit leaves them null/0 in the database while fees can be non-zero.
///
/// Secondary issue (lines 722-739): when both orders succeed but return FilledQuantity=0, the
/// guard correctly skips emergency-close and records zero fees but still sets
/// Status=EmergencyClosed instead of Failed, even though nothing was ever open on-chain.
/// </summary>
public class PhantomFeesRegressionTests
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
    private readonly Mock<IPnlReconciliationService> _mockReconciliation = new();
    private readonly ExecutionEngine _sut;

    /// <summary>Hyperliquid taker fee (0.045%). Used to assert non-zero expected fees.</summary>
    private const decimal HyperliquidFeeRate = 0.00045m;

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        UpdatedByUserId = "admin-user-id",
        OpenConfirmTimeoutSeconds = 30,
    };

    /// <summary>
    /// DefaultOpp: Hyperliquid (long) vs Lighter (short).
    /// Both connectors default to IsEstimatedFillExchange=false → concurrent execution path.
    /// </summary>
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

    public PhantomFeesRegressionTests()
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
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        _mockLongConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockShortConnector.Setup(c => c.ExchangeName).Returns("Lighter");

        // Both exchanges have ample balance
        _mockLongConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
        _mockShortConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);

        // Mark price and precision for quantity coordination
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

        // Default: both connectors return success (individual tests override as needed)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        // Both connectors confirm leg open for confirmation window
        _mockLongConnector
            .Setup(c => c.HasOpenPositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)true);
        _mockShortConnector
            .Setup(c => c.HasOpenPositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)true);

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

        _sut = new ExecutionEngine(
            _mockUow.Object, connectorLifecycle, emergencyClose, positionCloser,
            _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            mockBalanceAggregator.Object,
            NullLogger<ExecutionEngine>.Instance);
    }

    private static OrderResultDto SuccessOrder(string orderId = "1", decimal price = 3000m, decimal qty = 0.1m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = qty };

    private static OrderResultDto FailOrder(string error = "Insufficient margin") =>
        new() { Success = false, Error = error };

    // ── Concurrent path: one leg fills, other fails ────────────────────────────

    /// <summary>
    /// Phantom fee root cause #1 (ExecutionEngine.cs lines 650-657):
    /// When the long leg succeeds (non-zero fill) and the short leg fails, the engine calls
    /// SetEmergencyCloseFees using the long result — recording non-zero EntryFeesUsdc and
    /// ExitFeesUsdc — but never writes longTask.Result.FilledQuantity back to
    /// position.LongFilledQuantity.  The DB row therefore has LongFilledQuantity=0 while
    /// fees are positive: the phantom fee pattern.
    ///
    /// REQUIRED fix (Task 2.1): store position.LongFilledQuantity = longResult.FilledQuantity
    /// before (or alongside) calling SetEmergencyCloseFees in this branch.
    /// </summary>
    [Fact]
    public async Task PhantomFee_ConcurrentPath_WhenLongLegFillsAndShortFails_LongFilledQuantityShouldBeStoredOnPosition()
    {
        // Concurrent path: both connectors are non-estimated-fill (default mock)
        const decimal filledQty = 0.1m;
        const decimal filledPrice = 3000m;

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", filledPrice, filledQty));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short margin insufficient"));
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.EmergencyClosed);

        // The long leg DID fill — fees are computed from that fill.
        savedPos.EntryFeesUsdc.Should().BeGreaterThan(0m,
            "fees are recorded from the long leg's fill");

        // FAILING ASSERTION: LongFilledQuantity must match the actual fill so that
        // fee records have a corresponding quantity on the position entity.
        // Currently null/0 because lines 697-702 are never reached on this exit path.
        savedPos.LongFilledQuantity.Should().Be(filledQty,
            "the long leg filled {0} units — LongFilledQuantity must be stored so the " +
            "position is not a phantom-fee row (fees > 0 but filled qty = 0)", filledQty);
    }

    /// <summary>
    /// Phantom fee root cause #1 (symmetric): short leg fills, long leg fails.
    /// SetEmergencyCloseFees is called with shortTask.Result but position.ShortFilledQuantity
    /// is never assigned (ExecutionEngine.cs lines 659-665).
    /// </summary>
    [Fact]
    public async Task PhantomFee_ConcurrentPath_WhenShortLegFillsAndLongFails_ShortFilledQuantityShouldBeStoredOnPosition()
    {
        const decimal filledQty = 0.08m;

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long margin insufficient"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m, filledQty));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.EmergencyClosed);

        // FAILING ASSERTION: ShortFilledQuantity must reflect the actual fill.
        // Currently null/0 because lines 697-702 are never reached on this exit path.
        savedPos.ShortFilledQuantity.Should().Be(filledQty,
            "the short leg filled {0} units — ShortFilledQuantity must be stored to " +
            "avoid the phantom-fee data inconsistency", filledQty);
    }

    /// <summary>
    /// Phantom fee root cause #1 (exception variant — ExecutionEngine.cs lines 608-628):
    /// When one concurrent task throws and the other succeeds, the surviving leg undergoes
    /// emergency close.  SetEmergencyCloseFees records fees but the position's FilledQuantity
    /// field for the surviving leg is never written.
    /// </summary>
    [Fact]
    public async Task PhantomFee_ConcurrentPath_WhenShortThrowsAndLongSucceeds_LongFilledQuantityShouldBeStoredOnPosition()
    {
        const decimal filledQty = 0.1m;

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m, filledQty));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Short connector timed out"));
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.EmergencyClosed);

        // The long leg filled — fees will be non-zero (Hyperliquid has 0.00045m fee rate)
        var expectedEntryFee = filledQty * 3000m * HyperliquidFeeRate;
        savedPos.EntryFeesUsdc.Should().BeApproximately(expectedEntryFee, 0.0001m,
            "fees come from the long leg fill at price 3000 qty {0}", filledQty);

        // FAILING ASSERTION: LongFilledQuantity must be stored (not null/0).
        savedPos.LongFilledQuantity.Should().Be(filledQty,
            "the long leg filled {0} units even though the short threw — " +
            "LongFilledQuantity must be persisted alongside the fee record", filledQty);
    }

    // ── Sequential path: first leg fills, second leg fails ────────────────────

    /// <summary>
    /// Phantom fee root cause #1 (sequential path — ExecutionEngine.cs lines 520-526):
    /// When the second leg throws, the engine emergency-closes the first leg and calls
    /// SetEmergencyCloseFees.  The first leg's fill is recorded in fees but NOT written to
    /// position.LongFilledQuantity or position.ShortFilledQuantity.
    ///
    /// Sequential path is activated by setting IsEstimatedFillExchange=true on one connector.
    /// With Lighter as short and IsEstimatedFillExchange=true, firstIsLong evaluates to false
    /// so SHORT (Lighter) is the first leg and LONG (Hyperliquid) is the second.
    /// Hyperliquid throws → emergency close of Lighter → fees from Lighter (0m rate, so 0 fees),
    /// but the QUANTITY tracking bug is the same regardless of fee rate.
    ///
    /// REQUIRED fix (Task 2.2): store the first-leg's FilledQuantity on the appropriate
    /// position field in all emergency-close early-return branches.
    /// </summary>
    [Fact]
    public async Task PhantomFee_SequentialPath_WhenSecondLegThrowsAfterFirstLegFills_FirstLegFilledQuantityShouldBeStoredOnPosition()
    {
        // Sequential path: short (Lighter) is estimated-fill → goes first
        const decimal firstLegQty = 0.12m;
        _mockShortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m, firstLegQty));
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Long connector timed out"));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.EmergencyClosed);

        // FAILING ASSERTION: the short leg (first leg) filled — that quantity must be stored.
        // Currently null/0 because lines 697-702 are never reached in this exit path.
        savedPos.ShortFilledQuantity.Should().Be(firstLegQty,
            "the short leg (first leg) filled {0} units before the second leg threw — " +
            "ShortFilledQuantity must be persisted to prevent phantom-fee data inconsistency",
            firstLegQty);
    }

    /// <summary>
    /// Sequential path, second leg FAILS (not throws) — ExecutionEngine.cs lines 550-556.
    /// Same quantity-tracking gap: SetEmergencyCloseFees is called but position.ShortFilledQuantity
    /// remains unset.
    /// </summary>
    [Fact]
    public async Task PhantomFee_SequentialPath_WhenSecondLegFailsAfterFirstLegFills_FirstLegFilledQuantityShouldBeStoredOnPosition()
    {
        const decimal firstLegQty = 0.09m;
        _mockShortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m, firstLegQty));
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Long margin insufficient"));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.EmergencyClosed);

        // FAILING ASSERTION: ShortFilledQuantity must be stored.
        savedPos.ShortFilledQuantity.Should().Be(firstLegQty,
            "the short leg filled {0} units before the long leg failed — " +
            "ShortFilledQuantity must be persisted to prevent phantom-fee data inconsistency",
            firstLegQty);
    }

    // ── Zero-fill guard: both legs return qty=0 (lines 722–739) ───────────────

    /// <summary>
    /// Status bug (ExecutionEngine.cs line 739):
    /// When both legs return Success=true but FilledQuantity=0, the engine correctly skips
    /// emergency-close (nothing was ever open on-chain) and records zero fees.  However it
    /// still sets Status=EmergencyClosed.  Because no position was ever live on-chain, the
    /// correct terminal status is Failed — EmergencyClosed should be reserved for positions
    /// that required an actual emergency-close call.
    ///
    /// REQUIRED fix (Task 2.1): change line 739 to:
    ///   position.Status = (longQty > 0m || shortQty > 0m) ? PositionStatus.EmergencyClosed
    ///                                                      : PositionStatus.Failed;
    /// </summary>
    [Fact]
    public async Task PhantomFee_ZeroFillGuard_WhenBothLegsReturnZeroQuantity_StatusShouldBeFailedNotEmergencyClosed()
    {
        // Both legs succeed on the wire but fill zero (connector bug / IOC expired)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m, qty: 0m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("short-1", 3001m, qty: 0m));

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();

        // No emergency-close calls should have been made (nothing to close)
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "long leg filled zero — nothing to emergency-close");
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "short leg filled zero — nothing to emergency-close");

        // Fees must be zero (no on-chain activity occurred)
        savedPos!.EntryFeesUsdc.Should().Be(0m,
            "neither leg filled — no fees were incurred");
        savedPos.ExitFeesUsdc.Should().Be(0m);

        // FAILING ASSERTION: no position was ever live on-chain, so the status should be
        // Failed, not EmergencyClosed.  EmergencyClosed implies an actual close call was made.
        savedPos.Status.Should().Be(PositionStatus.Failed,
            "both legs returned zero fill — nothing was ever open on-chain, so the position " +
            "should be Failed, not EmergencyClosed (which implies an actual close was executed)");
    }

    // ── SetEmergencyCloseFees guard ───────────────────────────────────────────

    /// <summary>
    /// EmergencyCloseHandler.cs line 33: SetEmergencyCloseFees must not be called when
    /// the supplied OrderResultDto has FilledQuantity=0.  Even though the arithmetic produces
    /// zero fees (legNotional = price * 0 = 0), calling the method against a zero-quantity
    /// result is a logic error that can mask connector bugs.
    ///
    /// REQUIRED fix (Task 2.1): add an early-return guard inside SetEmergencyCloseFees:
    ///   if (successfulLeg.FilledQuantity &lt;= 0) return;
    ///
    /// This test verifies that after the guard is added, passing a zero-fill result leaves
    /// the position's RealizedPnl and fee fields untouched (null / default), distinguishing
    /// "guard fired — nothing happened" from "guard didn't fire but math gave zero".
    /// </summary>
    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsZero_ShouldLeaveRealizedPnlNull()
    {
        // Arrange — position with no prior PnL assignment
        var position = new ArbitragePosition { UserId = "user1" };
        var zeroFillResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0m,
        };

        // Act
        EmergencyCloseHandler.SetEmergencyCloseFees(position, zeroFillResult, "Hyperliquid");

        // The math gives zero fees (0 * price * rate = 0), which is correct.
        position.EntryFeesUsdc.Should().Be(0m);
        position.ExitFeesUsdc.Should().Be(0m);

        // FAILING ASSERTION: with a zero-fill guard the method must return early and leave
        // RealizedPnl null (its default).  Currently the method always writes
        // RealizedPnl = -(entryFee + exitFee) = 0, changing the field from null to 0.
        // A null RealizedPnl means "not yet computed"; 0 means "computed and equals zero" —
        // these are semantically different and the guard should preserve null.
        position.RealizedPnl.Should().BeNull(
            "zero-fill result means nothing was on-chain; RealizedPnl should remain null " +
            "(uncomputed) rather than being set to 0 by the fee calculation");
    }
}
