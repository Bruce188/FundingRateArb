using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class TradeAnalyticsServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IPositionRepository> _mockPositionRepo = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRateRepo = new();
    private readonly TradeAnalyticsService _sut;

    public TradeAnalyticsServiceTests()
    {
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRateRepo.Object);

        _mockFundingRateRepo.Setup(r => r.GetHourlyAggregatesAsync(
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _sut = new TradeAnalyticsService(_mockUow.Object);
    }

    [Fact]
    public void ComputeProjectedPnl_CalculatesCorrectly()
    {
        var pos = new ArbitragePosition
        {
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0.001m, // 0.1%/hr
        };

        var projected = TradeAnalyticsService.ComputeProjectedPnl(pos, hoursHeld: 24m);

        // 1000 * 0.001 * 24 = 24
        projected.Should().Be(24m);
    }

    [Fact]
    public void ComputeActualPnl_ClosedPosition_UsesRealizedPnl()
    {
        var pos = new ArbitragePosition
        {
            RealizedPnl = 15.5m,
            AccumulatedFunding = 20m, // should be ignored when RealizedPnl is set
        };

        var actual = TradeAnalyticsService.ComputeActualPnl(pos);

        actual.Should().Be(15.5m);
    }

    [Fact]
    public void ComputeActualPnl_OpenPosition_UsesAccumulatedFunding()
    {
        var pos = new ArbitragePosition
        {
            RealizedPnl = null,
            AccumulatedFunding = 8.2m,
        };

        var actual = TradeAnalyticsService.ComputeActualPnl(pos);

        actual.Should().Be(8.2m);
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_ClosedPosition_ComputesCorrectMetrics()
    {
        var openedAt = DateTime.UtcNow.AddHours(-48);
        var closedAt = DateTime.UtcNow.AddHours(-24);
        var pos = new ArbitragePosition
        {
            Id = 1,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 500m,
            EntrySpreadPerHour = 0.0005m,
            AccumulatedFunding = 10m,
            RealizedPnl = 5.5m,
            OpenedAt = openedAt,
            ClosedAt = closedAt,
            Status = PositionStatus.Closed,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Aster" },
        };

        _mockPositionRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(1);

        result.Should().NotBeNull();
        result!.PositionId.Should().Be(1);
        result.ActualPnl.Should().Be(5.5m); // uses RealizedPnl
        result.HoursHeld.Should().BeApproximately(24m, 0.1m);
        // Projected = 500 * 0.0005 * 24 = 6.0
        result.ProjectedPnl.Should().BeApproximately(6.0m, 0.1m);
        result.PnlDifference.Should().BeApproximately(-0.5m, 0.1m);
        result.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_OpenPosition_UsesAccumulatedFunding()
    {
        var openedAt = DateTime.UtcNow.AddHours(-12);
        var pos = new ArbitragePosition
        {
            Id = 2,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 1000m,
            EntrySpreadPerHour = 0.001m,
            AccumulatedFunding = 10m,
            RealizedPnl = null,
            OpenedAt = openedAt,
            ClosedAt = null,
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "BTC" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Lighter" },
        };

        _mockPositionRepo.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(2);

        result.Should().NotBeNull();
        result!.ActualPnl.Should().Be(10m); // uses AccumulatedFunding
        result.IsClosed.Should().BeFalse();
        // Projected = 1000 * 0.001 * ~12 = ~12
        result.ProjectedPnl.Should().BeApproximately(12m, 0.5m);
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_NonExistentPosition_ReturnsNull()
    {
        _mockPositionRepo.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((ArbitragePosition?)null);

        var result = await _sut.GetPositionAnalyticsAsync(999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_OwnerCanAccess()
    {
        var pos = new ArbitragePosition
        {
            Id = 10,
            UserId = "owner-123",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 500m,
            EntrySpreadPerHour = 0.001m,
            AccumulatedFunding = 5m,
            OpenedAt = DateTime.UtcNow.AddHours(-5),
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Aster" },
        };

        _mockPositionRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(10, "owner-123");

        result.Should().NotBeNull();
        result!.PositionId.Should().Be(10);
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_NonOwnerGetsNull()
    {
        var pos = new ArbitragePosition
        {
            Id = 11,
            UserId = "owner-123",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 500m,
            EntrySpreadPerHour = 0.001m,
            AccumulatedFunding = 5m,
            OpenedAt = DateTime.UtcNow.AddHours(-5),
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Aster" },
        };

        _mockPositionRepo.Setup(r => r.GetByIdAsync(11)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(11, "other-user-456");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_NullUserIdBypasses()
    {
        var pos = new ArbitragePosition
        {
            Id = 12,
            UserId = "owner-123",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 500m,
            EntrySpreadPerHour = 0.001m,
            AccumulatedFunding = 5m,
            OpenedAt = DateTime.UtcNow.AddHours(-5),
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Aster" },
        };

        _mockPositionRepo.Setup(r => r.GetByIdAsync(12)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(12, null);

        result.Should().NotBeNull();
        result!.PositionId.Should().Be(12);
    }

    [Fact]
    public async Task GetAllPositionAnalyticsAsync_WithUserId_FiltersPositions()
    {
        var positions = new List<ArbitragePosition>
        {
            new()
            {
                Id = 1, UserId = "user1", AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SizeUsdc = 100m, EntrySpreadPerHour = 0.001m, AccumulatedFunding = 5m,
                OpenedAt = DateTime.UtcNow.AddHours(-5), Status = PositionStatus.Open,
                Asset = new Asset { Symbol = "ETH" },
                LongExchange = new Exchange { Name = "Hyp" },
                ShortExchange = new Exchange { Name = "Ast" },
            },
        };

        _mockPositionRepo.Setup(r => r.GetByUserAsync("user1", 0, 50))
            .ReturnsAsync(positions);

        var results = await _sut.GetAllPositionAnalyticsAsync("user1", 0, 50);

        results.Should().HaveCount(1);
        results[0].PositionId.Should().Be(1);
        results[0].AccuracyPct.Should().NotBeNull();
    }
}
