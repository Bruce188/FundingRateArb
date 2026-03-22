using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Tests.Unit.Repositories;

public class BotConfigRepositoryCachingTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly BotConfigRepository _repository;

    public BotConfigRepositoryCachingTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _repository = new BotConfigRepository(_context, _cache);
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsCachedOnSecondCall()
    {
        // Seed DB
        _context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            TotalCapitalUsdc = 1000m,
            UpdatedByUserId = "admin"
        });
        await _context.SaveChangesAsync();

        // First call — loads from DB and populates cache
        var first = await _repository.GetActiveAsync();
        first.TotalCapitalUsdc.Should().Be(1000m);

        // Modify DB directly, bypassing cache
        var entity = await _context.BotConfigurations.FirstAsync();
        entity.TotalCapitalUsdc = 2000m;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Second call — should return cached value (1000m), not the updated DB value
        var second = await _repository.GetActiveAsync();
        second.TotalCapitalUsdc.Should().Be(1000m);

        // Defensive copy: the two returned objects should be different references
        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public async Task GetActiveAsync_LoadsFromDbOnCacheMiss()
    {
        // Seed DB
        _context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            TotalCapitalUsdc = 1000m,
            UpdatedByUserId = "admin"
        });
        await _context.SaveChangesAsync();

        // Call repository — cache is empty, should load from DB
        var result = await _repository.GetActiveAsync();

        result.Should().NotBeNull();
        result.TotalCapitalUsdc.Should().Be(1000m);
        result.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetActiveAsync_MutatingReturnedObject_DoesNotAffectCache()
    {
        // Seed DB
        _context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            TotalCapitalUsdc = 1000m,
            UpdatedByUserId = "admin"
        });
        await _context.SaveChangesAsync();

        // First call — loads from DB and populates cache
        var first = await _repository.GetActiveAsync();
        first.TotalCapitalUsdc.Should().Be(1000m);

        // Mutate the returned object
        first.TotalCapitalUsdc = 9999m;

        // Second call — should still return the original cached value, not the mutated one
        var second = await _repository.GetActiveAsync();
        second.TotalCapitalUsdc.Should().Be(1000m);
    }

    [Fact]
    public async Task InvalidateCache_ForcesDbReloadOnNextCall()
    {
        // Seed DB
        _context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            TotalCapitalUsdc = 1000m,
            UpdatedByUserId = "admin"
        });
        await _context.SaveChangesAsync();

        // Populate cache
        var first = await _repository.GetActiveAsync();
        first.TotalCapitalUsdc.Should().Be(1000m);

        // Modify DB directly
        var entity = await _context.BotConfigurations.FirstAsync();
        entity.TotalCapitalUsdc = 2000m;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Invalidate cache
        _repository.InvalidateCache();

        // Next call should reload from DB and return updated value
        var second = await _repository.GetActiveAsync();
        second.TotalCapitalUsdc.Should().Be(2000m);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _context.Dispose();
    }
}
