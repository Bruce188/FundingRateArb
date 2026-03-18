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

    public void Dispose() => _fixture.Dispose();
}
