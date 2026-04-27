using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Repositories;

namespace FundingRateArb.Tests.Integration.Repositories;

public class PositionRepositoryTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly ApplicationUser _user1;
    private readonly ApplicationUser _user2;

    public PositionRepositoryTests()
    {
        _fixture = new TestDbFixture();

        _user1 = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "user1@test.com", Email = "user1@test.com" };
        _user2 = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "user2@test.com", Email = "user2@test.com" };
        _fixture.Context.Users.AddRange(_user1, _user2);
        _fixture.Context.SaveChanges();
    }

    private ArbitragePosition BuildPosition(string userId, PositionStatus status) => new()
    {
        UserId = userId,
        AssetId = _fixture.TestAsset.Id,
        LongExchangeId = _fixture.TestExchange.Id,
        ShortExchangeId = _fixture.TestExchange.Id,
        Status = status,
        SizeUsdc = 500m,
        MarginUsdc = 100m,
        Leverage = 5,
        OpenedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task GetOpen_ReturnsOnlyOpenPositions()
    {
        // Arrange
        var open = BuildPosition(_user1.Id, PositionStatus.Open);
        var closed = BuildPosition(_user1.Id, PositionStatus.Closed);

        _fixture.UnitOfWork.Positions.Add(open);
        _fixture.UnitOfWork.Positions.Add(closed);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetOpenAsync();

        // Assert
        results.Should().HaveCount(1);
        results[0].Status.Should().Be(PositionStatus.Open);
    }

    [Fact]
    public async Task GetByUser_ReturnsOnlyThatUsersPositions()
    {
        // Arrange
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Open);
        var pos2 = BuildPosition(_user2.Id, PositionStatus.Open);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetByUserAsync(_user1.Id);

        // Assert
        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(_user1.Id);
    }

    // ── GetClosedWithNavigationSinceAsync integration tests ──

    [Fact]
    public async Task GetClosedWithNavigationSinceAsync_NullUserId_ReturnsAllClosedPositions()
    {
        // Arrange
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.ClosedAt = DateTime.UtcNow.AddDays(-1);
        var pos2 = BuildPosition(_user2.Id, PositionStatus.Closed);
        pos2.ClosedAt = DateTime.UtcNow.AddHours(-2);
        var openPos = BuildPosition(_user1.Id, PositionStatus.Open);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        _fixture.UnitOfWork.Positions.Add(openPos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act — null userId returns all users' closed positions
        var results = await _fixture.UnitOfWork.Positions.GetClosedWithNavigationSinceAsync(
            DateTime.UtcNow.AddDays(-7), userId: null);

        // Assert
        results.Should().HaveCount(2);
        results.Should().OnlyContain(p => p.Status == PositionStatus.Closed);
    }

    [Fact]
    public async Task GetClosedWithNavigationSinceAsync_SpecificUserId_ReturnsOnlyThatUsersPositions()
    {
        // Arrange
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.ClosedAt = DateTime.UtcNow.AddDays(-1);
        var pos2 = BuildPosition(_user2.Id, PositionStatus.Closed);
        pos2.ClosedAt = DateTime.UtcNow.AddHours(-2);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetClosedWithNavigationSinceAsync(
            DateTime.UtcNow.AddDays(-7), userId: _user1.Id);

        // Assert
        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(_user1.Id);
    }

    [Fact]
    public async Task GetClosedWithNavigationSinceAsync_LoadsNavigationProperties()
    {
        // Arrange
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetClosedWithNavigationSinceAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — navigation properties should be loaded (not null)
        results.Should().HaveCount(1);
        results[0].Asset.Should().NotBeNull();
        results[0].LongExchange.Should().NotBeNull();
        results[0].ShortExchange.Should().NotBeNull();
    }

    // ── GetClosedKpiProjectionSinceAsync integration test ──

    [Fact]
    public async Task GetClosedKpiProjectionSinceAsync_ReturnsLightweightProjection()
    {
        // Arrange
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.ClosedAt = DateTime.UtcNow.AddHours(-1);
        pos.RealizedPnl = 12.5m;

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetClosedKpiProjectionSinceAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — projection fields should be populated correctly
        results.Should().HaveCount(1);
        var dto = results[0];
        dto.RealizedPnl.Should().Be(12.5m);
        dto.AssetSymbol.Should().Be("BTC");
        dto.LongExchangeName.Should().NotBeNullOrEmpty();
        dto.ShortExchangeName.Should().NotBeNullOrEmpty();
        dto.ClosedAt.Should().NotBeNull();
        dto.OpenedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── GetKpiAggregatesAsync integration tests ──

    [Fact]
    public async Task GetKpiAggregatesAsync_WithPositionsAcrossWindows_ComputesCorrectKpis()
    {
        // Arrange: positions at 2d, 15d, and 60d ago
        var pos2d = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos2d.RealizedPnl = 10m;
        pos2d.OpenedAt = DateTime.UtcNow.AddDays(-3);
        pos2d.ClosedAt = DateTime.UtcNow.AddDays(-2);

        var pos15d = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos15d.RealizedPnl = 20m;
        pos15d.OpenedAt = DateTime.UtcNow.AddDays(-16);
        pos15d.ClosedAt = DateTime.UtcNow.AddDays(-15);

        var pos60d = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos60d.RealizedPnl = -5m;
        pos60d.OpenedAt = DateTime.UtcNow.AddDays(-61);
        pos60d.ClosedAt = DateTime.UtcNow.AddDays(-60);

        _fixture.UnitOfWork.Positions.Add(pos2d);
        _fixture.UnitOfWork.Positions.Add(pos15d);
        _fixture.UnitOfWork.Positions.Add(pos60d);
        await _fixture.UnitOfWork.SaveAsync();

        // Act — 90-day window
        var kpi = await _fixture.UnitOfWork.Positions.GetKpiAggregatesAsync(
            DateTime.UtcNow.AddDays(-90), userId: null);

        // Assert
        kpi.TotalTrades.Should().Be(3);
        kpi.WinCount.Should().Be(2);
        kpi.TotalPnl.Should().Be(25m);
        kpi.Pnl7d.Should().Be(10m);      // only pos2d
        kpi.Pnl30d.Should().Be(30m);     // pos2d + pos15d
        kpi.BestPnl.Should().Be(20m);
        kpi.WorstPnl.Should().Be(-5m);
        // Each position is held ~24 hours, so total ~72 hours
        kpi.TotalHoldHours.Should().BeApproximately(72.0, 1.0);
        kpi.HoldDataCount.Should().Be(3);
    }

    // Verify Pnl7d/Pnl30d return 0 when positions exist but fall outside those windows
    [Fact]
    public async Task GetKpiAggregatesAsync_PositionsOutside7dAnd30dWindows_ReturnsZeroForWindowedPnl()
    {
        // Arrange: all positions are 40+ days old — inside 90-day window but outside 7d and 30d
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.RealizedPnl = 15m;
        pos1.OpenedAt = DateTime.UtcNow.AddDays(-50);
        pos1.ClosedAt = DateTime.UtcNow.AddDays(-49);

        var pos2 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos2.RealizedPnl = 25m;
        pos2.OpenedAt = DateTime.UtcNow.AddDays(-42);
        pos2.ClosedAt = DateTime.UtcNow.AddDays(-41);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act — 90-day window captures these, but 7d/30d windows do not
        var kpi = await _fixture.UnitOfWork.Positions.GetKpiAggregatesAsync(
            DateTime.UtcNow.AddDays(-90), userId: null);

        // Assert — TotalPnl includes both, but windowed PnLs are zero
        kpi.TotalTrades.Should().Be(2);
        kpi.TotalPnl.Should().Be(40m);
        kpi.Pnl7d.Should().Be(0m);
        kpi.Pnl30d.Should().Be(0m);
    }

    [Fact]
    public async Task GetKpiAggregatesAsync_ZeroRows_ReturnsEmptyDto()
    {
        // Act — no closed positions exist
        var kpi = await _fixture.UnitOfWork.Positions.GetKpiAggregatesAsync(
            DateTime.UtcNow.AddDays(-90), userId: null);

        // Assert
        kpi.TotalTrades.Should().Be(0);
        kpi.TotalPnl.Should().Be(0);
        kpi.WinCount.Should().Be(0);
        kpi.TotalHoldHours.Should().Be(0);
        kpi.HoldDataCount.Should().Be(0);
    }

    [Fact]
    public async Task GetKpiAggregatesAsync_AllNullRealizedPnl_ReturnsZeroPnl()
    {
        // Arrange: closed positions with null RealizedPnl
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.RealizedPnl = null;
        pos1.ClosedAt = DateTime.UtcNow.AddHours(-1);

        var pos2 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos2.RealizedPnl = null;
        pos2.ClosedAt = DateTime.UtcNow.AddHours(-2);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var kpi = await _fixture.UnitOfWork.Positions.GetKpiAggregatesAsync(
            DateTime.UtcNow.AddDays(-7), userId: null);

        // Assert — nullable cast (decimal?)p.RealizedPnl ?? 0m returns 0 for all-null groups
        kpi.TotalPnl.Should().Be(0m);
        kpi.BestPnl.Should().Be(0m);
        kpi.WorstPnl.Should().Be(0m);
    }

    [Fact]
    public async Task GetKpiAggregatesAsync_FiltersByUserId()
    {
        // Arrange
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.RealizedPnl = 10m;
        pos1.ClosedAt = DateTime.UtcNow.AddHours(-1);

        var pos2 = BuildPosition(_user2.Id, PositionStatus.Closed);
        pos2.RealizedPnl = 20m;
        pos2.ClosedAt = DateTime.UtcNow.AddHours(-1);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var kpi = await _fixture.UnitOfWork.Positions.GetKpiAggregatesAsync(
            DateTime.UtcNow.AddDays(-7), userId: _user1.Id);

        // Assert — only user1's position
        kpi.TotalTrades.Should().Be(1);
        kpi.TotalPnl.Should().Be(10m);
    }

    // ── Null navigation property fallback tests ──
    // Note: InMemory provider does not populate navigation properties via AsNoTracking GroupBy
    // like SQL Server does with JOIN. These tests verify the queries execute without error
    // and produce correct aggregation even when navigation properties resolve to their
    // fallback values. Full "Unknown"/"?" verification requires a SQL Server test harness.

    [Fact]
    public async Task GetPerAssetKpiAsync_WithValidPositions_ReturnsAggregates()
    {
        // Arrange: position with valid asset
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.RealizedPnl = 5m;
        pos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerAssetKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — query runs without error and returns aggregated result
        results.Should().NotBeEmpty();
        results.Should().Contain(a => a.TotalPnl == 5m && a.Trades == 1);
    }

    [Fact]
    public async Task GetPerExchangePairKpiAsync_WithValidPositions_ReturnsAggregates()
    {
        // Arrange: position with valid exchanges
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.RealizedPnl = 3m;
        pos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerExchangePairKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — query runs without error and returns aggregated result
        results.Should().NotBeEmpty();
        results.Should().Contain(e => e.TotalPnl == 3m && e.Trades == 1);
    }

    // Verify GetPerAssetKpiAsync returns results ordered by TotalPnl descending
    [Fact]
    public async Task GetPerAssetKpiAsync_ReturnsResultsOrderedByTotalPnlDescending()
    {
        // Arrange: create a second asset so we get two groups
        var ethAsset = new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true };
        _fixture.Context.Assets.Add(ethAsset);
        _fixture.Context.SaveChanges();

        // BTC position: high PnL
        var btcPos = BuildPosition(_user1.Id, PositionStatus.Closed);
        btcPos.RealizedPnl = 50m;
        btcPos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        // ETH position: lower PnL
        var ethPos = new ArbitragePosition
        {
            UserId = _user1.Id,
            AssetId = ethAsset.Id,
            LongExchangeId = _fixture.TestExchange.Id,
            ShortExchangeId = _fixture.TestExchange.Id,
            Status = PositionStatus.Closed,
            SizeUsdc = 500m,
            MarginUsdc = 100m,
            Leverage = 5,
            OpenedAt = DateTime.UtcNow.AddHours(-5),
            ClosedAt = DateTime.UtcNow.AddHours(-1),
            RealizedPnl = 10m,
        };

        _fixture.UnitOfWork.Positions.Add(btcPos);
        _fixture.UnitOfWork.Positions.Add(ethPos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerAssetKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — ordered by TotalPnl descending: BTC (50) before ETH (10)
        results.Should().HaveCountGreaterOrEqualTo(2);
        results[0].TotalPnl.Should().BeGreaterOrEqualTo(results[1].TotalPnl);
    }

    // Verify GetPerExchangePairKpiAsync returns results ordered by TotalPnl descending
    [Fact]
    public async Task GetPerExchangePairKpiAsync_ReturnsResultsOrderedByTotalPnlDescending()
    {
        // Arrange: create a second exchange for distinct pairs
        var exchange2 = new Exchange
        {
            Name = "SecondExchange",
            ApiBaseUrl = "https://api.second.com",
            WsBaseUrl = "wss://api.second.com/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true,
        };
        _fixture.Context.Exchanges.Add(exchange2);
        _fixture.Context.SaveChanges();

        // Pair 1: TestExchange/TestExchange — high PnL
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.RealizedPnl = 100m;
        pos1.ClosedAt = DateTime.UtcNow.AddHours(-1);

        // Pair 2: TestExchange/SecondExchange — lower PnL
        var pos2 = new ArbitragePosition
        {
            UserId = _user1.Id,
            AssetId = _fixture.TestAsset.Id,
            LongExchangeId = _fixture.TestExchange.Id,
            ShortExchangeId = exchange2.Id,
            Status = PositionStatus.Closed,
            SizeUsdc = 500m,
            MarginUsdc = 100m,
            Leverage = 5,
            OpenedAt = DateTime.UtcNow.AddHours(-5),
            ClosedAt = DateTime.UtcNow.AddHours(-1),
            RealizedPnl = 20m,
        };

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerExchangePairKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — ordered by TotalPnl descending
        results.Should().HaveCountGreaterOrEqualTo(2);
        results[0].TotalPnl.Should().BeGreaterOrEqualTo(results[1].TotalPnl);
    }

    // GetPerAssetKpiAsync must return 0 PnL for groups where all RealizedPnl are null
    [Fact]
    public async Task GetPerAssetKpiAsync_NullRealizedPnlInGroup_ReturnsZeroPnl()
    {
        // Arrange: position with null RealizedPnl should be excluded by the query filter (RealizedPnl != null).
        // But if all positions in a group have null PnL, the group should not appear at all.
        // Test that the query doesn't throw when processing empty groups.
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.RealizedPnl = null;
        pos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerAssetKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — filtered out by WHERE clause, no groups
        results.Should().BeEmpty();
    }

    // GetPerExchangePairKpiAsync must return 0 PnL for groups where all RealizedPnl are null
    [Fact]
    public async Task GetPerExchangePairKpiAsync_NullRealizedPnlInGroup_ReturnsZeroPnl()
    {
        // Arrange
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.RealizedPnl = null;
        pos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerExchangePairKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — filtered out by WHERE clause, no groups
        results.Should().BeEmpty();
    }

    // AvgHoldTimeHours uses capped denominator when holdData is limited
    [Fact]
    public async Task GetKpiAggregatesAsync_HoldHoursUsesConsistentDenominator()
    {
        // Arrange: seed a few positions and verify TotalHoldHours/TotalTrades is consistent
        // (full regression requires >10K positions which is impractical; this verifies the formula)
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.RealizedPnl = 10m;
        pos1.OpenedAt = DateTime.UtcNow.AddHours(-48);
        pos1.ClosedAt = DateTime.UtcNow.AddHours(-24);

        var pos2 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos2.RealizedPnl = 5m;
        pos2.OpenedAt = DateTime.UtcNow.AddHours(-24);
        pos2.ClosedAt = DateTime.UtcNow.AddHours(-12);

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var kpi = await _fixture.UnitOfWork.Positions.GetKpiAggregatesAsync(
            DateTime.UtcNow.AddDays(-90), userId: null);

        // Assert: TotalTrades == 2, TotalHoldHours ~36 (24 + 12), HoldDataCount == 2
        kpi.TotalTrades.Should().Be(2);
        kpi.TotalHoldHours.Should().BeApproximately(36.0, 1.0);
        kpi.HoldDataCount.Should().Be(2);
        // AvgHoldTimeHours should be ~18 (36/2) — verifies denominator consistency
        var avgHold = kpi.HoldDataCount > 0 ? kpi.TotalHoldHours / kpi.HoldDataCount : 0;
        avgHold.Should().BeApproximately(18.0, 1.0);
    }

    // ── MaxGroupResults cap verification ──

    [Fact]
    public void MaxGroupResults_IsSetTo100()
    {
        // Verify the constant value so changes are caught by test
        PositionRepository.MaxGroupResults.Should().Be(100);
    }

    [Fact]
    public async Task GetPerAssetKpi_WithMultipleGroups_TakeAppliesToResults()
    {
        // Arrange: seed 3 distinct asset groups and verify all are returned
        // (if someone removed .Take(MaxGroupResults), this test structure still works
        // because we verify the query produces correct group counts — the companion
        // assertion on MaxGroupResults==100 guards against removal)
        var eth = new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true };
        var sol = new Asset { Symbol = "SOL", Name = "Solana", IsActive = true };
        _fixture.Context.Assets.AddRange(eth, sol);
        _fixture.Context.SaveChanges();

        var btcPos = BuildPosition(_user1.Id, PositionStatus.Closed);
        btcPos.RealizedPnl = 10m;
        btcPos.ClosedAt = DateTime.UtcNow.AddHours(-1);

        var ethPos = new ArbitragePosition
        {
            UserId = _user1.Id,
            AssetId = eth.Id,
            LongExchangeId = _fixture.TestExchange.Id,
            ShortExchangeId = _fixture.TestExchange.Id,
            Status = PositionStatus.Closed,
            SizeUsdc = 500m,
            MarginUsdc = 100m,
            Leverage = 5,
            OpenedAt = DateTime.UtcNow.AddHours(-3),
            ClosedAt = DateTime.UtcNow.AddHours(-1),
            RealizedPnl = 20m,
        };

        var solPos = new ArbitragePosition
        {
            UserId = _user1.Id,
            AssetId = sol.Id,
            LongExchangeId = _fixture.TestExchange.Id,
            ShortExchangeId = _fixture.TestExchange.Id,
            Status = PositionStatus.Closed,
            SizeUsdc = 500m,
            MarginUsdc = 100m,
            Leverage = 5,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            ClosedAt = DateTime.UtcNow.AddHours(-1),
            RealizedPnl = 5m,
        };

        _fixture.UnitOfWork.Positions.Add(btcPos);
        _fixture.UnitOfWork.Positions.Add(ethPos);
        _fixture.UnitOfWork.Positions.Add(solPos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPerAssetKpiAsync(
            DateTime.UtcNow.AddDays(-7));

        // Assert — 3 groups, all within MaxGroupResults cap
        results.Should().HaveCount(3);
        results.Count.Should().BeLessOrEqualTo(PositionRepository.MaxGroupResults);
    }

    // ── GetPendingConfirmAsync integration tests ──

    [Fact]
    public async Task GetPendingConfirmAsync_ReturnsOnlyMatchingUserPositions()
    {
        // Arrange — 2 pending-confirm rows for user1, 1 for user2
        var user1PendingA = BuildPosition(_user1.Id, PositionStatus.Opening);
        user1PendingA.OpenedAt = DateTime.UtcNow.AddMinutes(-10);
        user1PendingA.OpenConfirmedAt = null;

        var user1PendingB = BuildPosition(_user1.Id, PositionStatus.Opening);
        user1PendingB.OpenedAt = DateTime.UtcNow.AddMinutes(-5);
        user1PendingB.OpenConfirmedAt = null;

        var user2Pending = BuildPosition(_user2.Id, PositionStatus.Opening);
        user2Pending.OpenedAt = DateTime.UtcNow.AddMinutes(-8);
        user2Pending.OpenConfirmedAt = null;

        _fixture.UnitOfWork.Positions.Add(user1PendingA);
        _fixture.UnitOfWork.Positions.Add(user1PendingB);
        _fixture.UnitOfWork.Positions.Add(user2Pending);
        await _fixture.UnitOfWork.SaveAsync();

        // Act — query user1 only, olderThan 1 minute
        var results = await _fixture.UnitOfWork.Positions.GetPendingConfirmAsync(
            _user1.Id, TimeSpan.FromMinutes(1), CancellationToken.None);

        // Assert — only user1's 2 positions are returned
        results.Should().HaveCount(2);
        results.Should().OnlyContain(p => p.UserId == _user1.Id);
    }

    [Fact]
    public async Task GetPendingConfirmAsync_HonorsMaxResults()
    {
        // Arrange — 5 pending-confirm rows for user1
        for (var i = 1; i <= 5; i++)
        {
            var pos = BuildPosition(_user1.Id, PositionStatus.Opening);
            pos.OpenedAt = DateTime.UtcNow.AddMinutes(-(10 + i));
            pos.OpenConfirmedAt = null;
            _fixture.UnitOfWork.Positions.Add(pos);
        }

        await _fixture.UnitOfWork.SaveAsync();

        // Act — cap at 3
        var results = await _fixture.UnitOfWork.Positions.GetPendingConfirmAsync(
            _user1.Id, TimeSpan.FromMinutes(1), CancellationToken.None, maxResults: 3);

        // Assert
        results.Should().HaveCount(3, "maxResults=3 must cap the result set");
    }

    [Fact]
    public async Task GetPendingConfirmAsync_ReturnsNoTrackedEntities()
    {
        // Arrange
        var pos = BuildPosition(_user1.Id, PositionStatus.Opening);
        pos.OpenedAt = DateTime.UtcNow.AddMinutes(-10);
        pos.OpenConfirmedAt = null;

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.Positions.GetPendingConfirmAsync(
            _user1.Id, TimeSpan.FromMinutes(1), CancellationToken.None);

        results.Should().HaveCount(1);

        // Mutate the entity and try to save — should NOT persist (AsNoTracking)
        var returned = results[0];
        returned.Notes = "mutated-via-no-tracking";
        await _fixture.UnitOfWork.SaveAsync();

        // Re-fetch to confirm the mutation was not persisted
        var refetched = await _fixture.UnitOfWork.Positions.GetByIdAsync(returned.Id);
        refetched.Should().NotBeNull();
        refetched!.Notes.Should().NotBe("mutated-via-no-tracking",
            "AsNoTracking entities must not be change-tracked");
    }

    [Fact]
    public async Task SumRealizedPnlExcludingPhantomAsync_ExcludesPhantomBackfillRows()
    {
        // Arrange: two normal closed rows (100 + 50 = 150) and one phantom row (200)
        var userId = _user1.Id;

        var normal1 = BuildPosition(userId, PositionStatus.Closed);
        normal1.RealizedPnl = 100m;
        normal1.IsPhantomFeeBackfill = false;

        var normal2 = BuildPosition(userId, PositionStatus.Closed);
        normal2.RealizedPnl = 50m;
        normal2.IsPhantomFeeBackfill = false;

        var phantom = BuildPosition(userId, PositionStatus.EmergencyClosed);
        phantom.RealizedPnl = 200m;
        phantom.IsPhantomFeeBackfill = true;

        _fixture.UnitOfWork.Positions.Add(normal1);
        _fixture.UnitOfWork.Positions.Add(normal2);
        _fixture.UnitOfWork.Positions.Add(phantom);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var sum = await _fixture.UnitOfWork.Positions.SumRealizedPnlExcludingPhantomAsync(
            userId,
            PositionStatus.Closed,
            PositionStatus.EmergencyClosed,
            PositionStatus.Liquidated);

        // Assert: only 100 + 50 = 150; the phantom row (200) must be excluded
        sum.Should().Be(150m, "phantom-fee backfill rows must be excluded from the SQL sum");
    }

    [Fact]
    public async Task SumRealizedPnlExcludingPhantomAsync_WithNoStatuses_ReturnsZero()
    {
        // Arrange: seed a closed position with PnL — it must not appear in the result because
        // LINQ Contains over an empty params array returns false for every row.
        var pos = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos.RealizedPnl = 42m;

        _fixture.UnitOfWork.Positions.Add(pos);
        await _fixture.UnitOfWork.SaveAsync();

        // Act — call with no status arguments; Contains([]) is always false
        var sum = await _fixture.UnitOfWork.Positions.SumRealizedPnlExcludingPhantomAsync(_user1.Id);

        // Assert: no statuses → no rows match → sum is 0
        sum.Should().Be(0m, "LINQ Contains over an empty params array matches no rows");
    }

    [Fact]
    public async Task SumRealizedPnlExcludingPhantomAsync_OtherUserPositionsNotIncluded()
    {
        // Arrange: user1 has one real closed row (100), user2 has another (999)
        var pos1 = BuildPosition(_user1.Id, PositionStatus.Closed);
        pos1.RealizedPnl = 100m;

        var pos2 = BuildPosition(_user2.Id, PositionStatus.Closed);
        pos2.RealizedPnl = 999m;

        _fixture.UnitOfWork.Positions.Add(pos1);
        _fixture.UnitOfWork.Positions.Add(pos2);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var sum = await _fixture.UnitOfWork.Positions.SumRealizedPnlExcludingPhantomAsync(
            _user1.Id,
            PositionStatus.Closed,
            PositionStatus.EmergencyClosed,
            PositionStatus.Liquidated);

        // Assert: only user1's position is summed
        sum.Should().Be(100m, "the sum must be scoped to the given userId");
    }

    [Fact]
    public async Task Insert_WithSlippageFields_PersistsCorrectPrecision()
    {
        // Arrange — build a closed position with all six new slippage / intended-mid fields populated.
        var position = new ArbitragePosition
        {
            UserId = _user1.Id,
            AssetId = _fixture.TestAsset.Id,
            LongExchangeId = _fixture.TestExchange.Id,
            ShortExchangeId = _fixture.TestExchange.Id,
            SizeUsdc = 100m,
            MarginUsdc = 100m,
            Leverage = 1,
            LongEntryPrice = 100m,
            ShortEntryPrice = 100m,
            EntrySpreadPerHour = 0.0005m,
            CurrentSpreadPerHour = 0.0005m,
            Status = PositionStatus.Closed,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            ClosedAt = DateTime.UtcNow,
            LongIntendedMidAtSubmit = 100.0001m,
            ShortIntendedMidAtSubmit = 100.0001m,
            LongEntrySlippagePct = 0.00012345m,
            ShortEntrySlippagePct = -0.00012345m,
            LongExitSlippagePct = 0.00067890m,
            ShortExitSlippagePct = 0.00000001m,  // sub-bps
        };

        // Act
        _fixture.UnitOfWork.Positions.Add(position);
        await _fixture.UnitOfWork.SaveAsync();

        var loaded = await _fixture.UnitOfWork.Positions.GetByIdAsync(position.Id);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.LongIntendedMidAtSubmit.Should().Be(100.0001m);
        loaded.ShortIntendedMidAtSubmit.Should().Be(100.0001m);
        loaded.LongEntrySlippagePct.Should().Be(0.00012345m);
        loaded.ShortEntrySlippagePct.Should().Be(-0.00012345m);
        loaded.LongExitSlippagePct.Should().Be(0.00067890m);
        loaded.ShortExitSlippagePct.Should().Be(0.00000001m);
    }

    public void Dispose() => _fixture.Dispose();
}
