using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Integration-style tests that exercise the tier provider path end-to-end:
/// ExecutionEngine + real LeverageTierCache + real ConnectorLifecycleManager.
/// </summary>
public class LeverageTierBehaviorTests
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
    private readonly LeverageTierCache _tierCache = new(NullLogger<LeverageTierCache>.Instance);
    private readonly ExecutionEngine _sut;

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

    public LeverageTierBehaviorTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            DefaultLeverage = 10,
            MaxLeverageCap = 50,
            UpdatedByUserId = "admin-user-id",
        });

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
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 10 });

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        _mockLongConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockShortConnector.Setup(c => c.ExchangeName).Returns("Lighter");

        _mockLongConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        _mockShortConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);

        _mockLongConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _mockShortConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _mockLongConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);
        _mockShortConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "1", FilledPrice = 3000m, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "2", FilledPrice = 3001m, FilledQuantity = 0.1m });

        // Pre-populate tier cache: Hyperliquid allows 20x for small notionals, Lighter only allows 5x
        _tierCache.UpdateTiers("Hyperliquid", "ETH", new[]
        {
            new LeverageTier(0m, 100_000m, 20, 0.005m),
            new LeverageTier(100_000m, decimal.MaxValue, 10, 0.01m),
        });
        _tierCache.UpdateTiers("Lighter", "ETH", new[]
        {
            new LeverageTier(0m, decimal.MaxValue, 5, 0.02m),
        });

        var connectorLifecycle = new ConnectorLifecycleManager(
            _mockFactory.Object, _mockUserSettings.Object, _tierCache,
            NullLogger<ConnectorLifecycleManager>.Instance);
        var emergencyClose = new EmergencyCloseHandler(
            _mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);
        var positionCloser = new PositionCloser(
            _mockUow.Object, connectorLifecycle, _mockReconciliation.Object, NullLogger<PositionCloser>.Instance);

        _sut = new ExecutionEngine(_mockUow.Object, connectorLifecycle, emergencyClose, positionCloser, _mockUserSettings.Object, _tierCache, NullLogger<ExecutionEngine>.Instance);
    }

    // ── Test: Effective leverage constrained by most restrictive exchange tier ─

    [Fact]
    public async Task OpenPosition_TierConstraint_UsesMinOfBothExchangeTiers()
    {
        // User wants 10x, Hyperliquid tier allows 20x, Lighter tier allows 5x
        // Effective should be min(10, 50, 20, 5) = 5

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        // The order should have been placed with leverage = 5 (Lighter's limit)
        _mockLongConnector.Verify(
            c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockShortConnector.Verify(
            c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, It.IsAny<decimal>(), 5, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenPosition_TierConstraint_CreatesLeverageReducedAlert()
    {
        // User wants 10x, tier limit is 5x → leverage reduced alert
        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LeverageReduced &&
                al.Message!.Contains("10x to 5x"))),
            Times.Once);
    }

    // ── Test: MaxLeverageCap overrides exchange max ───────────────────────────

    [Fact]
    public async Task OpenPosition_MaxLeverageCap_OverridesHigherTierLeverage()
    {
        // Set MaxLeverageCap=3 (lower than any tier allows), user leverage=10
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            DefaultLeverage = 10,
            MaxLeverageCap = 3,
            UpdatedByUserId = "admin-user-id",
        });

        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        // MaxLeverageCap=3 is applied before tier lookup, so leverage = min(10, 3) = 3
        // The tier check (5x Lighter limit) doesn't further reduce since 3 < 5
        _mockLongConnector.Verify(
            c => c.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, It.IsAny<decimal>(), 3, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test: Position sizing reduces leverage when tier is lower than config ──

    [Fact]
    public async Task OpenPosition_TierLowered_PositionSavedWithClampedLeverage()
    {
        // Verify that the saved ArbitragePosition entity has the clamped leverage
        ArbitragePosition? saved = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => saved = p);

        await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.Leverage.Should().Be(5); // clamped from 10x to 5x by Lighter tier
    }
}
