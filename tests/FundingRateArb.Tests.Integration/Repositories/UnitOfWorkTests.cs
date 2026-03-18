using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Integration.Repositories;

public class UnitOfWorkTests : IDisposable
{
    private readonly TestDbFixture _fixture;

    public UnitOfWorkTests()
    {
        _fixture = new TestDbFixture();
    }

    [Fact]
    public async Task SaveAsync_PersistsAddedEntities()
    {
        // Arrange
        var snapshot = new FundingRateSnapshot
        {
            ExchangeId = _fixture.TestExchange.Id,
            AssetId = _fixture.TestAsset.Id,
            RatePerHour = 0.0002m,
            RecordedAt = DateTime.UtcNow
        };

        // Act
        _fixture.UnitOfWork.FundingRates.Add(snapshot);
        var rowsAffected = await _fixture.UnitOfWork.SaveAsync();

        // Assert
        rowsAffected.Should().Be(1);
        _fixture.Context.FundingRateSnapshots.Should().HaveCount(1);
    }

    [Fact]
    public async Task Repositories_ReturnSameContextData()
    {
        // Arrange — add asset via AssetRepository
        var newAsset = new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true };
        _fixture.UnitOfWork.Assets.Add(newAsset);
        await _fixture.UnitOfWork.SaveAsync();

        // Act — read via a fresh query on same UoW
        var assets = await _fixture.UnitOfWork.Assets.GetAllAsync();

        // Assert — should see the newly added asset
        assets.Should().Contain(a => a.Symbol == "ETH");
    }

    public void Dispose() => _fixture.Dispose();
}
