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

public class ExecutionEngineClientOrderIdTests
{
    private const string TestUserId = "test-user-id";
    private const int ExpectedPositionId = 1;

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

    public ExecutionEngineClientOrderIdTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => p.Id = ExpectedPositionId);

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

        // Default: concurrent path (no IsEstimatedFillExchange)
        _mockLongConnector.Setup(c => c.IsEstimatedFillExchange).Returns(false);
        _mockShortConnector.Setup(c => c.IsEstimatedFillExchange).Returns(false);

        // Default: both legs succeed
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "exch-long", FilledPrice = 3000m, FilledQuantity = 0.1m });
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "exch-short", FilledPrice = 3000m, FilledQuantity = 0.1m });

        // Default: HasOpenPositionAsync returns true (used if concurrent path needs confirmation)
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
        var emergencyClose = new EmergencyCloseHandler(_mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);
        var positionCloser = new PositionCloser(_mockUow.Object, connectorLifecycle, _mockReconciliation.Object, NullLogger<PositionCloser>.Instance);
        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { Balances = new List<ExchangeBalanceDto>(), TotalAvailableUsdc = 1000m, FetchedAt = DateTime.UtcNow });

        _sut = new ExecutionEngine(_mockUow.Object, connectorLifecycle, emergencyClose, positionCloser,
            _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue),
            mockBalanceAggregator.Object,
            NullLogger<ExecutionEngine>.Instance);
    }

    [Fact]
    public async Task OpenPositionAsync_GeneratesIdFromPositionIdAndSideAndAttemptN_PassesToConnector()
    {
        // Arrange: capture the clientOrderId arguments passed to both connectors
        string? capturedLongClientOrderId = null;
        string? capturedShortClientOrderId = null;

        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(
                It.IsAny<string>(), Side.Long, It.IsAny<decimal>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, string?, CancellationToken>(
                (asset, side, qty, lev, coid, ct) => capturedLongClientOrderId = coid)
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "exch-long", FilledPrice = 3000m, FilledQuantity = 0.1m });

        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(
                It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Side, decimal, int, string?, CancellationToken>(
                (asset, side, qty, lev, coid, ct) => capturedShortClientOrderId = coid)
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "exch-short", FilledPrice = 3000m, FilledQuantity = 0.1m });

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        capturedLongClientOrderId.Should().Be(OrderIdGenerator.For(ExpectedPositionId, Side.Long, 1));
        capturedShortClientOrderId.Should().Be(OrderIdGenerator.For(ExpectedPositionId, Side.Short, 1));
    }

    [Fact]
    public async Task OpenPositionAsync_OnSuccess_IncrementsBothAttemptCounters()
    {
        // Arrange: capture the saved position
        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => { p.Id = ExpectedPositionId; savedPos = p; });

        // Act
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        savedPos.Should().NotBeNull();
        savedPos!.LongOrderAttemptN.Should().Be(1);
        savedPos.ShortOrderAttemptN.Should().Be(1);
    }

    [Fact]
    public async Task OpenPositionAsync_RetryWithExistingOrder_ReusesId()
    {
        // Arrange: mock connector always returns the same OrderId regardless of input
        // (simulating "exchange returned existing order for this clientOrderId")
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(
                It.IsAny<string>(), Side.Long, It.IsAny<decimal>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "existing-order-id", FilledPrice = 3000m, FilledQuantity = 0.1m });

        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(
                It.IsAny<string>(), Side.Short, It.IsAny<decimal>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "existing-order-id", FilledPrice = 3000m, FilledQuantity = 0.1m });

        // Act: should succeed — the bot should accept the result without alerting / failing
        var result = await _sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        // Assert: position opened successfully, no LegFailed alert fired
        result.Success.Should().BeTrue();
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.LegFailed)), Times.Never);
    }
}
