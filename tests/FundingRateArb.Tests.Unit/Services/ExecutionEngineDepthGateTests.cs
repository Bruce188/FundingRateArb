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
/// Tests for the depth-gate integration in ExecutionEngine.
/// Verifies gate-reject (no PlaceMarketOrder call), gate-pass (flow proceeds),
/// post-fill revert feedback, and spot-leverage clamp.
/// </summary>
public class ExecutionEngineDepthGateTests
{
    private const string TestUserId = "depth-gate-user";
    private const string Asset = "ETH";

    private static readonly ArbitrageOpportunityDto DefaultOpp = new()
    {
        AssetSymbol = Asset,
        AssetId = 1,
        LongExchangeName = "Lighter",
        LongExchangeId = 1,
        ShortExchangeName = "Hyperliquid",
        ShortExchangeId = 2,
        SpreadPerHour = 0.0005m,
        NetYieldPerHour = 0.0004m,
        LongMarkPrice = 3000m,
        ShortMarkPrice = 3000m,
        LongVolume24h = 1_000_000m,
        ShortVolume24h = 1_000_000m,
    };

    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IBotConfigRepository> _botConfig = new();
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IAlertRepository> _alerts = new();
    private readonly Mock<IUserSettingsService> _userSettings = new();
    private readonly Mock<IExchangeConnector> _longConnector = new();
    private readonly Mock<IExchangeConnector> _shortConnector = new();
    private readonly Mock<IExchangeConnectorFactory> _factory = new();
    private readonly Mock<IPreflightSlippageGuard> _preflightGuard = new();
    private readonly Mock<IPnlReconciliationService> _reconciliation = new();

    public ExecutionEngineDepthGateTests()
    {
        var config = new BotConfiguration
        {
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 1,
            MaxLeverageCap = 5,
            UpdatedByUserId = "admin",
            OpenConfirmTimeoutSeconds = 5,
        };

        _uow.Setup(u => u.BotConfig).Returns(_botConfig.Object);
        _uow.Setup(u => u.Positions).Returns(_positions.Object);
        _uow.Setup(u => u.Alerts).Returns(_alerts.Object);
        _uow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _botConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _positions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => p.Id = 1);
        _positions.Setup(p => p.Update(It.IsAny<ArbitragePosition>()));
        _alerts.Setup(a => a.Add(It.IsAny<Alert>()));

        _userSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 1 });

        // Default connector setup: perp, no depth gate opt-in (returns null → gate skipped)
        _longConnector.Setup(c => c.ExchangeName).Returns("Lighter");
        _longConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);
        _longConnector.Setup(c => c.IsEstimatedFillExchange).Returns(true);
        _longConnector.Setup(c => c.GetOrderbookDepthAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderbookDepthSnapshot?)null);
        _longConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _longConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(4);
        _longConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        _longConnector.Setup(c => c.GetMaxLeverageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((int?)10);

        _shortConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _shortConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);
        _shortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(false);
        _shortConnector.Setup(c => c.GetOrderbookDepthAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderbookDepthSnapshot?)null);
        _shortConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _shortConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(4);
        _shortConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        _shortConnector.Setup(c => c.GetMaxLeverageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((int?)10);

        _factory
            .Setup(f => f.GetConnector(It.IsAny<string>()))
            .Returns<string>(name => name == "Lighter" ? _longConnector.Object : _shortConnector.Object);
    }

    private ExecutionEngine CreateEngine(IPreflightSlippageGuard? guard = null)
    {
        guard ??= _preflightGuard.Object;

        var mockBalance = new Mock<IBalanceAggregator>();
        mockBalance.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                Balances = new List<ExchangeBalanceDto>(),
                TotalAvailableUsdc = 1000m,
                FetchedAt = DateTime.UtcNow,
            });

        var mockLifecycle = new Mock<IConnectorLifecycleManager>();
        mockLifecycle
            .Setup(l => l.CreateUserConnectorsAsync(It.IsAny<string>(), "Lighter", "Hyperliquid"))
            .ReturnsAsync((_longConnector.Object, _shortConnector.Object, (string?)null));
        mockLifecycle
            .Setup(l => l.WrapForDryRun(It.IsAny<IExchangeConnector>(), It.IsAny<IExchangeConnector>()))
            .Returns<IExchangeConnector, IExchangeConnector>((l, s) => (l, s));
        mockLifecycle
            .Setup(l => l.EnsureTiersCachedAsync(It.IsAny<IExchangeConnector>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockLifecycle
            .Setup(l => l.GetCachedMaxLeverageAsync(It.IsAny<IExchangeConnector>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        var mockEmergencyClose = new Mock<IEmergencyCloseHandler>();
        var mockPositionCloser = new Mock<IPositionCloser>();

        return new ExecutionEngine(
            _uow.Object,
            mockLifecycle.Object,
            mockEmergencyClose.Object,
            mockPositionCloser.Object,
            _userSettings.Object,
            Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            mockBalance.Object,
            guard,
            NullLogger<ExecutionEngine>.Instance);
    }

    // ── Gate REJECT: PlaceMarketOrderByQuantityAsync NOT called ──────────────

    [Fact]
    public async Task DepthGate_Rejects_PlaceOrderIsNotCalled()
    {
        // Long connector returns InsufficientDepth snapshot → gate rejects
        var insufficientSnapshot = new OrderbookDepthSnapshot(
            Asset, Side.Long, 3000m, 0m, 0m, OrderbookDepthSource.Insufficient);
        _longConnector
            .Setup(c => c.GetOrderbookDepthAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(insufficientSnapshot);

        var engine = CreateEngine();
        var (success, error) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        success.Should().BeFalse();
        error.Should().Contain("Insufficient");
        _longConnector.Verify(
            c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "PlaceMarketOrderByQuantityAsync must NOT be called when the depth gate rejects");
    }

    [Fact]
    public async Task DepthGate_RejectsOnSlippage_PlaceOrderIsNotCalled()
    {
        // Long connector returns snapshot that exceeds slippage cap
        var slippageSnapshot = new OrderbookDepthSnapshot(
            Asset, Side.Long, 3000m, 3015m, 0.005m, OrderbookDepthSource.WsCache);
        _longConnector
            .Setup(c => c.GetOrderbookDepthAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(slippageSnapshot);

        // Guard says: 0.005 > cap (0.003) → reject
        _preflightGuard.Setup(g => g.ShouldReject("Lighter", Asset, 0.005m)).Returns(true);
        _preflightGuard.Setup(g => g.GetCurrentCap("Lighter", Asset)).Returns(0.003m);

        var engine = CreateEngine();
        var (success, error) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        success.Should().BeFalse();
        _longConnector.Verify(
            c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Gate PASS: flow proceeds normally ────────────────────────────────────

    [Fact]
    public async Task DepthGate_Passes_PlaceOrderIsCalled()
    {
        // Both connectors return null (default opt-out) → gate is skipped entirely
        // PlaceMarketOrder is called and returns success
        _longConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "L1", FilledPrice = 3000m, FilledQuantity = 0.033m, IsEstimatedFill = true });
        _shortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "S1", FilledPrice = 3000m, FilledQuantity = 0.033m });
        _longConnector.Setup(c => c.HasOpenPositionAsync(Asset, Side.Long, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)true);
        _shortConnector.Setup(c => c.HasOpenPositionAsync(Asset, Side.Short, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)true);

        var engine = CreateEngine();
        var (success, _) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        // Both legs called since gate passes (null → skip)
        _longConnector.Verify(
            c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Post-fill revert feedback ────────────────────────────────────────────

    [Fact]
    public async Task DepthGate_PostFillSlippageRevert_CallsRecordRevert()
    {
        // Long leg reverts with Slippage reason
        _longConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = false,
                Error = "Slippage revert",
                RevertReason = LighterOrderRevertReason.Slippage,
                IsEstimatedFill = true,
            });

        var engine = CreateEngine();
        await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        _preflightGuard.Verify(
            g => g.RecordRevert("Lighter", Asset, LighterOrderRevertReason.Slippage),
            Times.Once,
            "RecordRevert must be called when first leg reverts with Slippage reason");
    }

    [Fact]
    public async Task DepthGate_PostFillInsufficientDepthRevert_CallsRecordRevert()
    {
        _longConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = false,
                Error = "InsufficientDepth revert",
                RevertReason = LighterOrderRevertReason.InsufficientDepth,
                IsEstimatedFill = true,
            });

        var engine = CreateEngine();
        await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        _preflightGuard.Verify(
            g => g.RecordRevert("Lighter", Asset, LighterOrderRevertReason.InsufficientDepth),
            Times.Once);
    }

    [Fact]
    public async Task DepthGate_PostFillMarginRevert_DoesNotCallRecordRevert()
    {
        _longConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = false,
                Error = "Margin revert",
                RevertReason = LighterOrderRevertReason.MarginInsufficient,
                IsEstimatedFill = true,
            });

        var engine = CreateEngine();
        await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        _preflightGuard.Verify(
            g => g.RecordRevert(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LighterOrderRevertReason>()),
            Times.Never,
            "RecordRevert must NOT be called for non-depth revert reasons");
    }

    // ── Spot-leverage clamp ──────────────────────────────────────────────────

    [Fact]
    public async Task SpotLeverageClamp_SpotLongLeg_ClampsToOne()
    {
        // Long connector is Spot
        _longConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Spot);

        // With config leverage=5 and a spot long leg, effective leverage should be clamped to 1.
        // The easiest way to observe this: the order is placed with effectiveLeverage=1.
        // Set up a "success" flow so we can verify the lever passed to PlaceOrder.
        int capturedLeverage = -1;
        _longConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Long, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, string?, CancellationToken>((_, _, _, lev, _, _) => capturedLeverage = lev)
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "L1", FilledPrice = 3000m, FilledQuantity = 0.033m, IsEstimatedFill = true });
        _shortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(Asset, Side.Short, It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "S1", FilledPrice = 3000m, FilledQuantity = 0.033m });
        _longConnector.Setup(c => c.HasOpenPositionAsync(Asset, Side.Long, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)true);
        _shortConnector.Setup(c => c.HasOpenPositionAsync(Asset, Side.Short, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)true);

        // Use a BotConfiguration with DefaultLeverage=5
        var configWith5xLeverage = new BotConfiguration
        {
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 5,
            MaxLeverageCap = 10,
            UpdatedByUserId = "admin",
            OpenConfirmTimeoutSeconds = 5,
        };
        _botConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(configWith5xLeverage);
        _userSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5 });

        var engine = CreateEngine();
        await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        capturedLeverage.Should().Be(1, "spot leg must clamp leverage to 1 regardless of configuration");
    }
}
