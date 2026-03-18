using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Tests.Integration.Repositories;

public class FundingRateRepositoryTests : IDisposable
{
    private readonly TestDbFixture _fixture;

    public FundingRateRepositoryTests()
    {
        _fixture = new TestDbFixture();
    }

    [Fact]
    public async Task GetLatestPerExchangePerAsset_ReturnsOnlyMostRecent()
    {
        // Arrange
        var older = new FundingRateSnapshot
        {
            ExchangeId = _fixture.TestExchange.Id,
            AssetId = _fixture.TestAsset.Id,
            RatePerHour = 0.0001m,
            RawRate = 0.0001m,
            RecordedAt = DateTime.UtcNow.AddHours(-2)
        };
        var newer = new FundingRateSnapshot
        {
            ExchangeId = _fixture.TestExchange.Id,
            AssetId = _fixture.TestAsset.Id,
            RatePerHour = 0.0003m,
            RawRate = 0.0003m,
            RecordedAt = DateTime.UtcNow.AddHours(-1)
        };

        _fixture.UnitOfWork.FundingRates.Add(older);
        _fixture.UnitOfWork.FundingRates.Add(newer);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.FundingRates.GetLatestPerExchangePerAssetAsync();

        // Assert
        results.Should().HaveCount(1);
        results[0].RatePerHour.Should().Be(0.0003m);
    }

    [Fact]
    public async Task GetLatestPerExchangePerAsset_ReturnsOnePerExchangeAssetPair()
    {
        // Arrange — add a second exchange
        var exchange2 = new Exchange
        {
            Name = "TestExchange2",
            ApiBaseUrl = "https://api.test2.com",
            WsBaseUrl = "wss://api.test2.com/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1
        };
        _fixture.Context.Exchanges.Add(exchange2);
        await _fixture.Context.SaveChangesAsync();

        _fixture.UnitOfWork.FundingRates.AddRange([
            new FundingRateSnapshot { ExchangeId = _fixture.TestExchange.Id, AssetId = _fixture.TestAsset.Id, RatePerHour = 0.0001m, RecordedAt = DateTime.UtcNow },
            new FundingRateSnapshot { ExchangeId = exchange2.Id, AssetId = _fixture.TestAsset.Id, RatePerHour = 0.0004m, RecordedAt = DateTime.UtcNow }
        ]);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var results = await _fixture.UnitOfWork.FundingRates.GetLatestPerExchangePerAssetAsync();

        // Assert — one per exchange-asset pair
        results.Should().HaveCount(2);
        results.Select(r => r.ExchangeId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetHistory_FiltersCorrectDateRange()
    {
        // Arrange
        var inRange = new FundingRateSnapshot
        {
            ExchangeId = _fixture.TestExchange.Id,
            AssetId = _fixture.TestAsset.Id,
            RatePerHour = 0.0002m,
            RecordedAt = DateTime.UtcNow.AddHours(-5)
        };
        var outOfRange = new FundingRateSnapshot
        {
            ExchangeId = _fixture.TestExchange.Id,
            AssetId = _fixture.TestAsset.Id,
            RatePerHour = 0.0001m,
            RecordedAt = DateTime.UtcNow.AddHours(-25)
        };

        _fixture.UnitOfWork.FundingRates.Add(inRange);
        _fixture.UnitOfWork.FundingRates.Add(outOfRange);
        await _fixture.UnitOfWork.SaveAsync();

        var from = DateTime.UtcNow.AddHours(-12);
        var to = DateTime.UtcNow;

        // Act
        var results = await _fixture.UnitOfWork.FundingRates.GetHistoryAsync(
            _fixture.TestAsset.Id, _fixture.TestExchange.Id, from, to);

        // Assert
        results.Should().HaveCount(1);
        results[0].RatePerHour.Should().Be(0.0002m);
    }

    public void Dispose() => _fixture.Dispose();
}
