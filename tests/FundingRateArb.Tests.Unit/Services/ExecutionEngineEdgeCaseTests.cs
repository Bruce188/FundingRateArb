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

public class ExecutionEngineEdgeCaseTests
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

    public ExecutionEngineEdgeCaseTests()
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

        _sut = new ExecutionEngine(_mockUow.Object, _mockFactory.Object, NullLogger<ExecutionEngine>.Instance);
    }

    private static OrderResultDto SuccessOrder(string orderId = "1", decimal price = 3000m, decimal qty = 0.1m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = qty };

    private static OrderResultDto FailOrder(string error = "Exchange down") =>
        new() { Success = false, Error = error };

    // ── Test 1: Both legs fail ─────────────────────────────────────────────────

    [Fact]
    public async Task OpenPosition_WhenBothLegsFail_ReturnsFailureWithError()
    {
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Exchange down"));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Exchange down"));

        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        _mockPositions.Verify(p => p.Add(It.IsAny<ArbitragePosition>()), Times.Never);
    }

    // ── Test 2: Emergency close of long leg throws HttpRequestException ────────

    [Fact]
    public async Task OpenPosition_EmergencyCloseFailure_DoesNotThrow()
    {
        // Long leg succeeds, short leg fails
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("long-1", 3000m));
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailOrder("Short exchange down"));

        // Emergency close of the long leg throws
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error during emergency close"));

        // Act — must not throw
        Func<Task> act = async () => await _sut.OpenPositionAsync(DefaultOpp, 100m);

        await act.Should().NotThrowAsync();

        // Assert: returns failure
        var result = await _sut.OpenPositionAsync(DefaultOpp, 100m);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();

        // Assert: a Critical LegFailed alert is saved (called at least once across the two invocations)
        _mockAlerts.Verify(
            a => a.Add(It.Is<Alert>(al =>
                al.Type == AlertType.LegFailed &&
                al.Severity == AlertSeverity.Critical)),
            Times.AtLeastOnce);
    }

    // ── Test 3: Both legs closed concurrently ─────────────────────────────────

    [Fact]
    public async Task ClosePosition_ConcurrentLegs_BothClosed()
    {
        var position = MakeOpenPosition();

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-long", 100m, 1m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder("close-short", 100m, 1m));

        await _sut.ClosePositionAsync(position, CloseReason.Manual);

        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()),
            Times.Once);

        position.Status.Should().Be(PositionStatus.Closed);
        position.RealizedPnl.Should().NotBeNull();
    }

    // ── Test 4: Nav props null → queries DB for exchange/asset names ───────────

    [Fact]
    public async Task ClosePosition_WhenNavPropsNull_QueriesDBForNames()
    {
        // Position with null navigation properties
        var position = new ArbitragePosition
        {
            Id = 99,
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
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            LongExchange = null!,   // intentionally null — must be resolved from DB
            ShortExchange = null!, // intentionally null — must be resolved from DB
            Asset = null!,         // intentionally null — must be resolved from DB
        };

        _mockExchanges
            .Setup(r => r.GetByIdAsync(1))
            .ReturnsAsync(new Exchange { Id = 1, Name = "Hyperliquid" });
        _mockExchanges
            .Setup(r => r.GetByIdAsync(2))
            .ReturnsAsync(new Exchange { Id = 2, Name = "Lighter" });
        _mockAssets
            .Setup(r => r.GetByIdAsync(1))
            .ReturnsAsync(new Asset { Id = 1, Symbol = "ETH" });

        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        await _sut.ClosePositionAsync(position, CloseReason.Manual);

        _mockExchanges.Verify(r => r.GetByIdAsync(It.IsAny<int>()), Times.AtLeastOnce);
        _mockAssets.Verify(r => r.GetByIdAsync(It.IsAny<int>()), Times.AtLeastOnce);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
}
