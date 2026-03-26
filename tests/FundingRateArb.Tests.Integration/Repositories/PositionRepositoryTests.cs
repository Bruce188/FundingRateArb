using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

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

    // ── NB8: GetClosedWithNavigationSinceAsync integration tests ──

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

    // ── NB6: GetClosedKpiProjectionSinceAsync integration test ──

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

    // ── NB4: GetKpiAggregatesAsync integration tests ──

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
        kpi.TotalHoldHours.Should().BeGreaterThan(0);
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

    // ── NB6: Null navigation property fallback tests ──
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

    public void Dispose() => _fixture.Dispose();
}
