using System.Reflection;
using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
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

    [Fact]
    public async Task ShallowCopy_CopiesAllProperties()
    {
        // Build a BotConfiguration with every settable property set to a non-default value
        // so we can detect any property omitted from the ShallowCopy method.
        var source = new BotConfiguration
        {
            Id = 42,
            IsEnabled = true,
            OpenThreshold = 0.999m,
            AlertThreshold = 0.888m,
            CloseThreshold = -0.0009m,
            StopLossPct = 0.77m,
            MaxHoldTimeHours = 99,
            VolumeFraction = 0.05m,
            MaxCapitalPerPosition = 0.55m,
            BreakevenHoursMax = 100,
            TotalCapitalUsdc = 5555m,
            DefaultLeverage = 7,
            MaxConcurrentPositions = 8,
            AllocationStrategy = AllocationStrategy.EqualSpread,
            AllocationTopN = 11,
            FeeAmortizationHours = 36,
            MinPositionSizeUsdc = 99m,
            MinVolume24hUsdc = 123_456m,
            RateStalenessMinutes = 60,
            DailyDrawdownPausePct = 0.22m,
            ConsecutiveLossPause = 7,
            FundingWindowMinutes = 30,
            MaxExposurePerAsset = 0.33m,
            MaxExposurePerExchange = 0.44m,
            TargetPnlMultiplier = 5.5m,
            AdaptiveHoldEnabled = true,
            RebalanceEnabled = true,
            RebalanceMinImprovement = 0.001m,
            MaxRebalancesPerCycle = 10,
            ExchangeCircuitBreakerThreshold = 5,
            ExchangeCircuitBreakerMinutes = 30,
            MinHoldBeforePnlTargetMinutes = 120,
            EmergencyCloseSpreadThreshold = -0.005m,
            PriceFeedFailureCloseThreshold = 20,
            SlippageBufferBps = 10,
            LiquidationWarningPct = 0.40m,
            ReconciliationIntervalCycles = 20,
            DivergenceAlertMultiplier = 3.0m,
            UseRiskBasedDivergenceClose = false, // flip from default `true` so ShallowCopy gap is detectable
            MinConsecutiveFavorableCycles = 5,
            FundingFlipExitCycles = 4,
            StablecoinAlertThresholdPct = 0.5m,
            StablecoinCriticalThresholdPct = 2.0m,
            MinHoldTimeHours = 4,
            DryRunEnabled = true,
            ForceConcurrentExecution = true,
            LastUpdatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            UpdatedByUserId = "test-user-id",
        };

        // Seed with the custom config
        _context.BotConfigurations.Add(source);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // GetActiveAsync internally calls ShallowCopy
        var copy = await _repository.GetActiveAsync();

        // Use reflection to verify every public settable property was copied
        var properties = typeof(BotConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            var expected = prop.GetValue(source);
            var actual = prop.GetValue(copy);
            actual.Should().Be(expected,
                because: $"ShallowCopy must copy property '{prop.Name}'");
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        _context.Dispose();
    }
}

public class BotConfigRepositoryOrderingTests
{
    private static (AppDbContext context, IMemoryCache cache, BotConfigRepository repository) CreateFixture()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var context = new AppDbContext(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new BotConfigRepository(context, cache);
        return (context, cache, repository);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GetActiveAsync_MultipleRows_ReturnsLowestIdDeterministically(int _)
    {
        var (context, cache, repository) = CreateFixture();
        try
        {
            context.BotConfigurations.AddRange(
                new BotConfiguration { IsEnabled = true, TotalCapitalUsdc = 100m, UpdatedByUserId = "admin" },
                new BotConfiguration { IsEnabled = true, TotalCapitalUsdc = 200m, UpdatedByUserId = "admin" }
            );
            await context.SaveChangesAsync();

            var allIds = context.BotConfigurations.Select(c => c.Id).OrderBy(id => id).ToList();
            var lowestId = allIds.First();

            var result = await repository.GetActiveAsync();

            result.Should().NotBeNull();
            result!.Id.Should().Be(lowestId);
        }
        finally
        {
            cache.Dispose();
            context.Dispose();
        }
    }

    [Fact]
    public async Task GetActiveTrackedAsync_MultipleRows_ReturnsLowestIdDeterministically()
    {
        var (context, cache, repository) = CreateFixture();
        try
        {
            context.BotConfigurations.AddRange(
                new BotConfiguration { IsEnabled = true, TotalCapitalUsdc = 100m, UpdatedByUserId = "admin" },
                new BotConfiguration { IsEnabled = true, TotalCapitalUsdc = 200m, UpdatedByUserId = "admin" }
            );
            await context.SaveChangesAsync();

            var lowestId = context.BotConfigurations.Select(c => c.Id).OrderBy(id => id).First();

            var result = await repository.GetActiveTrackedAsync();

            result.Should().NotBeNull();
            result!.Id.Should().Be(lowestId);
            context.Entry(result).State.Should().NotBe(EntityState.Detached);
        }
        finally
        {
            cache.Dispose();
            context.Dispose();
        }
    }

    [Fact]
    public async Task GetActiveAsync_SingleRow_StillReturnsThatRow()
    {
        var (context, cache, repository) = CreateFixture();
        try
        {
            context.BotConfigurations.Add(new BotConfiguration
            {
                IsEnabled = true,
                TotalCapitalUsdc = 500m,
                UpdatedByUserId = "admin"
            });
            await context.SaveChangesAsync();

            var result = await repository.GetActiveAsync();

            result.Should().NotBeNull();
            result!.TotalCapitalUsdc.Should().Be(500m);
        }
        finally
        {
            cache.Dispose();
            context.Dispose();
        }
    }
}
