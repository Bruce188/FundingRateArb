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

    // ── Task 3.1: EmergencyClosed maps IsClosed=true and StatusLabel ─────────

    [Fact]
    public async Task GetAllPositionAnalyticsAsync_EmergencyClosed_MapsIsClosedTrueAndStatusLabel()
    {
        var positions = new List<ArbitragePosition>
        {
            new()
            {
                Id = 20, UserId = "user1", AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SizeUsdc = 100m, EntrySpreadPerHour = 0.001m, AccumulatedFunding = 0m,
                OpenedAt = DateTime.UtcNow.AddHours(-1), ClosedAt = DateTime.UtcNow,
                Status = PositionStatus.EmergencyClosed,
                Asset = new Asset { Symbol = "ETH" },
                LongExchange = new Exchange { Name = "Hyp" },
                ShortExchange = new Exchange { Name = "Lighter" },
            },
        };

        _mockPositionRepo.Setup(r => r.GetAllAsync(0, 50)).ReturnsAsync(positions);

        var results = await _sut.GetAllPositionAnalyticsAsync(null, 0, 50);

        results.Should().HaveCount(1);
        results[0].IsClosed.Should().BeTrue("EmergencyClosed positions should map IsClosed=true");
        results[0].StatusLabel.Should().Be("EmergencyClosed");
    }

    [Fact]
    public async Task GetAllPositionAnalyticsAsync_OpeningPosition_MapsIsClosedFalseAndStatusLabel()
    {
        var positions = new List<ArbitragePosition>
        {
            new()
            {
                Id = 21, UserId = "user1", AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SizeUsdc = 100m, EntrySpreadPerHour = 0.001m, AccumulatedFunding = 0m,
                OpenedAt = DateTime.UtcNow.AddMinutes(-1), ClosedAt = null,
                Status = PositionStatus.Opening,
                Asset = new Asset { Symbol = "BTC" },
                LongExchange = new Exchange { Name = "Hyp" },
                ShortExchange = new Exchange { Name = "Aster" },
            },
        };

        _mockPositionRepo.Setup(r => r.GetAllAsync(0, 50)).ReturnsAsync(positions);

        var results = await _sut.GetAllPositionAnalyticsAsync(null, 0, 50);

        results.Should().HaveCount(1);
        results[0].IsClosed.Should().BeFalse("Opening positions should map IsClosed=false");
        results[0].StatusLabel.Should().Be("Opening");
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

    // ── Counterfactual PnL computation ──────────────────────────────────────

    [Fact]
    public async Task ComputeCounterfactualPnlAsync_MatchesFormula_ForKnownInputs()
    {
        var closeTime = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        var position = new ArbitragePosition
        {
            Id = 30,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 1000m,
            ClosedAt = closeTime,
            Status = PositionStatus.Closed,
        };
        var actualPnl = 5.0m;

        // Post-close spread data: 3 hours of data after close
        var longAggs = new List<FundingRateHourlyAggregate>
        {
            new() { HourUtc = closeTime.AddHours(0), AvgRatePerHour = 0.0002m },
            new() { HourUtc = closeTime.AddHours(1), AvgRatePerHour = 0.0003m },
            new() { HourUtc = closeTime.AddHours(2), AvgRatePerHour = 0.0001m },
        };
        var shortAggs = new List<FundingRateHourlyAggregate>
        {
            new() { HourUtc = closeTime.AddHours(0), AvgRatePerHour = 0.0005m },
            new() { HourUtc = closeTime.AddHours(1), AvgRatePerHour = 0.0006m },
            new() { HourUtc = closeTime.AddHours(2), AvgRatePerHour = 0.0004m },
        };

        _mockFundingRateRepo.Setup(r => r.GetHourlyAggregatesAsync(
                1, 1, closeTime, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(longAggs);
        _mockFundingRateRepo.Setup(r => r.GetHourlyAggregatesAsync(
                1, 2, closeTime, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shortAggs);

        var result = await _sut.ComputeCounterfactualPnlAsync(position, actualPnl, CancellationToken.None);

        result.Should().HaveCount(4); // +1h, +4h, +24h, +48h

        // +1h: only hour 0 counts (HourUtc < closeTime + 1h)
        // spread at h0 = 0.0005 - 0.0002 = 0.0003
        // hypothetical = 5.0 + (0.0003 * 1000) = 5.0 + 0.3 = 5.3
        result[0].Label.Should().Be("+1h");
        result[0].HypotheticalPnl.Should().Be(5.0m + 0.0003m * 1000m);

        // +4h: all 3 hours count (HourUtc < closeTime + 4h)
        // cumulative spread = 0.0003 + 0.0003 + 0.0003 = 0.0009
        var cumSpread = (0.0005m - 0.0002m) + (0.0006m - 0.0003m) + (0.0004m - 0.0001m);
        result[1].Label.Should().Be("+4h");
        result[1].HypotheticalPnl.Should().Be(5.0m + cumSpread * 1000m);

        // +24h and +48h: same as +4h since only 3 hours of data
        result[2].Label.Should().Be("+24h");
        result[2].HypotheticalPnl.Should().Be(result[1].HypotheticalPnl);
        result[3].Label.Should().Be("+48h");
        result[3].HypotheticalPnl.Should().Be(result[1].HypotheticalPnl);
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_OpenPosition_ReturnsEmptyCounterfactuals()
    {
        var openedAt = DateTime.UtcNow.AddHours(-12);
        var pos = new ArbitragePosition
        {
            Id = 50,
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

        _mockPositionRepo.Setup(r => r.GetByIdAsync(50)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(50);

        result.Should().NotBeNull();
        result!.IsClosed.Should().BeFalse();
        result.Counterfactuals.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeCounterfactualPnlAsync_ReturnsEmpty_WhenClosedAtNull()
    {
        var position = new ArbitragePosition
        {
            Id = 40,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 1000m,
            ClosedAt = null,
            Status = PositionStatus.Open,
        };

        var result = await _sut.ComputeCounterfactualPnlAsync(position, 5.0m, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeCounterfactualPnlAsync_HandlesMismatchedHourBuckets()
    {
        var closeTime = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        var position = new ArbitragePosition
        {
            Id = 41,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 1000m,
            ClosedAt = closeTime,
            Status = PositionStatus.Closed,
        };

        // Long has data at h0, h1, h2; short only has h0 and h2 (partial overlap)
        var longAggs = new List<FundingRateHourlyAggregate>
        {
            new() { HourUtc = closeTime.AddHours(0), AvgRatePerHour = 0.0002m },
            new() { HourUtc = closeTime.AddHours(1), AvgRatePerHour = 0.0003m },
            new() { HourUtc = closeTime.AddHours(2), AvgRatePerHour = 0.0001m },
        };
        var shortAggs = new List<FundingRateHourlyAggregate>
        {
            new() { HourUtc = closeTime.AddHours(0), AvgRatePerHour = 0.0005m },
            // h1 missing — simulates partial hour overlap
            new() { HourUtc = closeTime.AddHours(2), AvgRatePerHour = 0.0004m },
        };

        _mockFundingRateRepo.Setup(r => r.GetHourlyAggregatesAsync(
                1, 1, closeTime, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(longAggs);
        _mockFundingRateRepo.Setup(r => r.GetHourlyAggregatesAsync(
                1, 2, closeTime, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shortAggs);

        var result = await _sut.ComputeCounterfactualPnlAsync(position, 5.0m, CancellationToken.None);

        result.Should().HaveCount(4);

        // +1h: only h0 matches (h1 is missing from short)
        // spread at h0 = 0.0005 - 0.0002 = 0.0003
        result[0].HypotheticalPnl.Should().Be(5.0m + 0.0003m * 1000m);

        // +4h: h0 and h2 match (h1 skipped because short has no data)
        // cumulative = (0.0005-0.0002) + (0.0004-0.0001) = 0.0003 + 0.0003 = 0.0006
        var cumSpread = (0.0005m - 0.0002m) + (0.0004m - 0.0001m);
        result[1].HypotheticalPnl.Should().Be(5.0m + cumSpread * 1000m);
    }

    [Fact]
    public async Task GetPositionAnalyticsAsync_ClosedPosition_PopulatesCounterfactuals()
    {
        var openedAt = DateTime.UtcNow.AddHours(-48);
        var closedAt = DateTime.UtcNow.AddHours(-24);
        var pos = new ArbitragePosition
        {
            Id = 31,
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

        _mockPositionRepo.Setup(r => r.GetByIdAsync(31)).ReturnsAsync(pos);

        var result = await _sut.GetPositionAnalyticsAsync(31);

        result.Should().NotBeNull();
        result!.IsClosed.Should().BeTrue();
        // Even with empty spread data, counterfactuals should be populated (all = actualPnl)
        result.Counterfactuals.Should().HaveCount(4);
        result.Counterfactuals.Should().AllSatisfy(cf =>
            cf.HypotheticalPnl.Should().Be(5.5m, "no post-close spread data means PnL stays at actual"));
    }
}
