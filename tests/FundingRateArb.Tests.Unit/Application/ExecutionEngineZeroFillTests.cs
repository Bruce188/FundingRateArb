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
/// Regression suite for phantom-fee bug fix (task 2.1).
///
/// Covers three scenarios mandated by the spec:
///   1. Both legs zero-fill  → Status == Failed, all fees zero.
///   2. Long leg filled, short zero → Status == EmergencyClosed, fees from filled leg.
///   3. Both legs filled (happy-path) → existing behaviour unchanged.
///
/// The "both legs zero-fill" scenario is tested through BOTH the direct concurrent
/// path (both return Success=true with FilledQuantity=0) and through the intermediate
/// concurrent failure path (long returns 0-fill, short fails entirely) — the latter
/// path currently assigns PositionStatus.EmergencyClosed instead of Failed because the
/// intermediate guard at lines 652–674 does not check effective fill quantities.
/// </summary>
public class ExecutionEngineZeroFillTests
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

    private static readonly BotConfiguration DefaultConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        UpdatedByUserId = "admin-user-id",
        OpenConfirmTimeoutSeconds = 30,
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

    public ExecutionEngineZeroFillTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig);

        var longCred = new UserExchangeCredential
        {
            Id = 1,
            ExchangeId = 1,
            Exchange = new Exchange { Name = "Hyperliquid" },
        };
        var shortCred = new UserExchangeCredential
        {
            Id = 2,
            ExchangeId = 2,
            Exchange = new Exchange { Name = "Lighter" },
        };
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
            .Setup(f => f.CreateForUserAsync("Hyperliquid",
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter",
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
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

        // Default: both legs succeed with a real fill (overridden per-test where needed)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(
                It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ZeroFillOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(
                It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ZeroFillOrder());

        // Both-leg confirmation window: return true immediately so happy-path tests don't time out
        _mockLongConnector
            .Setup(c => c.HasOpenPositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)true);
        _mockShortConnector
            .Setup(c => c.HasOpenPositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)true);

        var connectorLifecycle = new ConnectorLifecycleManager(
            _mockFactory.Object,
            _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p =>
                p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            NullLogger<ConnectorLifecycleManager>.Instance);

        var emergencyClose = new EmergencyCloseHandler(
            _mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);

        var positionCloser = new PositionCloser(
            _mockUow.Object, connectorLifecycle, _mockReconciliation.Object,
            NullLogger<PositionCloser>.Instance);

        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                Balances = new List<ExchangeBalanceDto>(),
                TotalAvailableUsdc = 1000m,
                FetchedAt = DateTime.UtcNow,
            });

        _sut = new ExecutionEngine(
            _mockUow.Object,
            connectorLifecycle,
            emergencyClose,
            positionCloser,
            _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p =>
                p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            mockBalanceAggregator.Object,
            NullLogger<ExecutionEngine>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OrderResultDto FilledOrder(
        string orderId = "1", decimal price = 3000m, decimal qty = 0.1m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = qty };

    private static OrderResultDto ZeroFillOrder(
        string orderId = "1", decimal price = 3000m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = 0m };

    private static OrderResultDto FailOrder(string error = "Exchange down") =>
        new() { Success = false, Error = error };

    // ── Scenario 1: Both legs zero-fill ──────────────────────────────────────

    /// <summary>
    /// Direct path: both concurrent legs report Success=true but FilledQuantity=0.
    /// The zero-fill end-guard (lines 719–760) must set Status=Failed, not EmergencyClosed,
    /// and must not write any fee values.
    /// </summary>
    [Fact]
    public async Task OpenPosition_BothLegsZeroFill_DirectConcurrentPath_StatusIsFailedAndFeesAreZero()
    {
        // Both legs succeed but with zero-fill (connector bug or on-chain lot-size rejection)
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ZeroFillOrder("long-1"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ZeroFillOrder("short-1"));

        ArbitragePosition? savedPos = null;
        _mockPositions
            .Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(
            TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.Failed,
            "both legs reported zero fill — no position was ever opened on either exchange");
        savedPos.EntryFeesUsdc.Should().Be(0m,
            "no fee can be incurred when neither leg filled any quantity");
        savedPos.ExitFeesUsdc.Should().Be(0m,
            "no exit fee can be incurred when neither leg opened a position");

        // Neither leg should have been called to emergency-close (nothing to close)
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "long leg was never opened (zero fill) — no emergency close needed");
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "short leg was never opened (zero fill) — no emergency close needed");
    }

    /// <summary>
    /// Intermediate concurrent-failure path: long leg reports Success=true but
    /// FilledQuantity=0; short leg fails outright (Success=false).  The emergency-close
    /// call for the long leg confirms the position momentarily existed
    /// (neverExisted=false).  Even so, because <em>both</em> effective fill quantities
    /// are zero the engine must set Status=Failed — not EmergencyClosed — and must not
    /// write any fee values.
    ///
    /// Current bug: lines 652–674 set concurrentNeverExisted=false whenever the
    /// emergency-close branch fires, without checking whether the surviving leg actually
    /// filled any quantity.  This causes Status=EmergencyClosed even when both effective
    /// quantities are 0.
    /// </summary>
    [Fact]
    public async Task OpenPosition_LongZeroFillShortFails_BothEffectivelyZeroFilled_StatusMustBeFailed()
    {
        // Long reports success but with zero fill — connector acknowledged the order
        // but nothing actually traded (on-chain lot-size or IOC expiry).
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ZeroFillOrder("long-1", price: 3000m));

        // Short leg fails entirely
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short exchange unavailable"));

        // Emergency-close of the long leg "succeeds" — the position briefly existed
        // (neverExisted=false) even though it had zero fill.
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true });

        ArbitragePosition? savedPos = null;
        _mockPositions
            .Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(
            TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();

        // *** KEY ASSERTION — currently fails: engine sets EmergencyClosed instead of Failed ***
        savedPos!.Status.Should().Be(PositionStatus.Failed,
            "both effective fill quantities are 0; even though the emergency-close path fired, " +
            "no real position existed — Status must be Failed, not EmergencyClosed");

        savedPos.EntryFeesUsdc.Should().Be(0m,
            "long leg had zero fill: SetEmergencyCloseFees must not write fees for a zero-fill leg");
        savedPos.ExitFeesUsdc.Should().Be(0m,
            "no trade occurred on either leg — exit fees must not be written");
    }

    // ── Scenario 2: Long leg filled, short zero-fill ─────────────────────────

    /// <summary>
    /// One real position exists (long, qty=0.1) while the short leg returns zero fill.
    /// The engine must emergency-close the surviving long position, set Status=EmergencyClosed,
    /// and compute fees exclusively from the long leg's actual fill.
    /// </summary>
    [Fact]
    public async Task OpenPosition_LongFilledShortZeroFill_StatusIsEmergencyClosed_FeesFromLongLeg()
    {
        // Long filled with a real quantity; short returned Success=true but qty=0
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilledOrder("long-1", price: 3000m, qty: 0.1m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ZeroFillOrder("short-1", price: 3001m));

        // Emergency-close of the long leg (surviving leg) succeeds
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilledOrder("close-long", price: 3000m, qty: 0.1m));

        ArbitragePosition? savedPos = null;
        _mockPositions
            .Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(
            TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.EmergencyClosed,
            "the long leg was actually filled — a real position existed and was emergency-closed");

        // Fees are computed from the long leg (Hyperliquid rate = 0.00045)
        savedPos.EntryFeesUsdc.Should().BeGreaterThan(0m,
            "the filled long leg incurred an entry fee that must be recorded");
        savedPos.ExitFeesUsdc.Should().BeGreaterThan(0m,
            "the emergency close of the filled long leg incurred an exit fee");
        savedPos.RealizedPnl.Should().BeLessThan(0m,
            "emergency close at roughly the same price leaves a net fee loss");

        // Long (surviving) was closed; short (zero-fill) must never be closed
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once,
            "the surviving long leg must be emergency-closed exactly once");
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "short leg had zero fill — nothing to close");
    }

    // ── Scenario 3: Both legs filled (happy-path unchanged) ──────────────────

    /// <summary>
    /// Confirms the normal happy-path is unaffected by the zero-fill guards:
    /// when both legs fill with real quantities the position must reach Status=Open
    /// with fees correctly computed from both legs.
    /// </summary>
    [Fact]
    public async Task OpenPosition_BothLegsFilled_HappyPathIsUnchanged()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilledOrder("long-1", price: 3000m, qty: 0.1m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short,
                It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilledOrder("short-1", price: 3001m, qty: 0.1m));

        ArbitragePosition? savedPos = null;
        _mockPositions
            .Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var result = await _sut.OpenPositionAsync(
            TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        savedPos.Should().NotBeNull();
        savedPos!.Status.Should().Be(PositionStatus.Open,
            "both legs filled — position must transition to Open");
        savedPos.EntryFeesUsdc.Should().BeGreaterThan(0m,
            "a real fill on Hyperliquid incurs a taker entry fee");
        savedPos.LongFilledQuantity.Should().Be(0.1m);
        savedPos.ShortFilledQuantity.Should().Be(0.1m);
    }
}
