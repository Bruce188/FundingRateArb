using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Tests.Integration.Repositories;

public class TestDbFixture : IDisposable
{
    public AppDbContext Context { get; }
    public UnitOfWork UnitOfWork { get; }
    public Exchange TestExchange { get; }
    public Asset TestAsset { get; }

    public TestDbFixture()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        Context = new AppDbContext(options);

        TestExchange = new Exchange
        {
            Name = "TestExchange",
            ApiBaseUrl = "https://api.test.com",
            WsBaseUrl = "wss://api.test.com/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        };

        TestAsset = new Asset
        {
            Symbol = "BTC",
            Name = "Bitcoin",
            IsActive = true
        };

        Context.Exchanges.Add(TestExchange);
        Context.Assets.Add(TestAsset);
        Context.SaveChanges();

        UnitOfWork = new UnitOfWork(Context, new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose()
    {
        UnitOfWork.Dispose();
        Context.Dispose();
    }
}
