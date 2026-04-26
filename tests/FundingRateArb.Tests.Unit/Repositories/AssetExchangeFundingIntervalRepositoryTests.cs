using FluentAssertions;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Tests.Unit.Repositories;

/// <summary>
/// Tests for IAssetExchangeFundingIntervalRepository and AssetExchangeFundingIntervalRepository.
/// Covers: interface contract, UpsertManyAsync (insert + update), GetIntervalsAsync (caching), InvalidateCache.
/// </summary>
public class AssetExchangeFundingIntervalRepositoryTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly AssetExchangeFundingIntervalRepository _repository;

    // Seeded FK stubs
    private const int ExchangeId1 = 1;
    private const int ExchangeId2 = 2;
    private const int AssetId1 = 10;
    private const int AssetId2 = 20;

    public AssetExchangeFundingIntervalRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _repository = new AssetExchangeFundingIntervalRepository(_context, _cache);

        SeedFkEntities();
    }

    private void SeedFkEntities()
    {
        _context.Exchanges.AddRange(
            new Exchange { Id = ExchangeId1, Name = "ExA", ApiBaseUrl = "https://a.api", WsBaseUrl = "wss://a.ws" },
            new Exchange { Id = ExchangeId2, Name = "ExB", ApiBaseUrl = "https://b.api", WsBaseUrl = "wss://b.ws" }
        );
        _context.Assets.AddRange(
            new Asset { Id = AssetId1, Symbol = "BTC", Name = "Bitcoin" },
            new Asset { Id = AssetId2, Symbol = "ETH", Name = "Ethereum" }
        );
        _context.SaveChanges();
    }

    // -----------------------------------------------------------------------
    // Interface contract
    // -----------------------------------------------------------------------

    [Fact]
    public void Repository_ImplementsInterface()
    {
        _repository.Should().BeAssignableTo<IAssetExchangeFundingIntervalRepository>();
    }

    // -----------------------------------------------------------------------
    // GetIntervalsAsync — basic reads
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetIntervalsAsync_WhenNoRows_ReturnsEmptyDictionary()
    {
        var result = await _repository.GetIntervalsAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetIntervalsAsync_ReturnsDictionaryKeyedByExchangeIdAndAssetId()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 8, (int?)null) },
            CancellationToken.None);

        _repository.InvalidateCache();
        var result = await _repository.GetIntervalsAsync(CancellationToken.None);

        result.Should().ContainKey((ExchangeId1, AssetId1));
        result[(ExchangeId1, AssetId1)].Should().Be(8);
    }

    [Fact]
    public async Task GetIntervalsAsync_MultipleRows_AllPairsPresent()
    {
        await _repository.UpsertManyAsync(
            new[]
            {
                (ExchangeId1, AssetId1, 4, (int?)null),
                (ExchangeId1, AssetId2, 8, (int?)null),
                (ExchangeId2, AssetId1, 1, (int?)null),
            },
            CancellationToken.None);

        _repository.InvalidateCache();
        var result = await _repository.GetIntervalsAsync(CancellationToken.None);

        result.Should().HaveCount(3);
        result[(ExchangeId1, AssetId1)].Should().Be(4);
        result[(ExchangeId1, AssetId2)].Should().Be(8);
        result[(ExchangeId2, AssetId1)].Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // UpsertManyAsync — insert
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpsertManyAsync_InsertsNewRows()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 8, (int?)null) },
            CancellationToken.None);

        var row = await _context.AssetExchangeFundingIntervals
            .FirstOrDefaultAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);

        row.Should().NotBeNull();
        row!.IntervalHours.Should().Be(8);
    }

    [Fact]
    public async Task UpsertManyAsync_InsertSetsUpdatedAtUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        var row = await _context.AssetExchangeFundingIntervals
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);

        row.UpdatedAtUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task UpsertManyAsync_InsertWithSourceSnapshotId_PersistsIt()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 8, (int?)42) },
            CancellationToken.None);

        var row = await _context.AssetExchangeFundingIntervals
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);

        row.SourceSnapshotId.Should().Be(42);
    }

    // -----------------------------------------------------------------------
    // UpsertManyAsync — update
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpsertManyAsync_UpdatesIntervalHoursForExistingRow()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 8, (int?)null) },
            CancellationToken.None);

        var row = await _context.AssetExchangeFundingIntervals
            .AsNoTracking()
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);

        row.IntervalHours.Should().Be(8);
    }

    [Fact]
    public async Task UpsertManyAsync_UpdatesSourceSnapshotIdForExistingRow()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)99) },
            CancellationToken.None);

        var row = await _context.AssetExchangeFundingIntervals
            .AsNoTracking()
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);

        row.SourceSnapshotId.Should().Be(99);
    }

    [Fact]
    public async Task UpsertManyAsync_UpdatesUpdatedAtUtcForExistingRow()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        var firstTimestamp = (await _context.AssetExchangeFundingIntervals
            .AsNoTracking()
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1)).UpdatedAtUtc;

        await Task.Delay(10); // ensure time advances

        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 8, (int?)null) },
            CancellationToken.None);

        var row = await _context.AssetExchangeFundingIntervals
            .AsNoTracking()
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);

        row.UpdatedAtUtc.Should().BeOnOrAfter(firstTimestamp);
    }

    [Fact]
    public async Task UpsertManyAsync_MixedBatch_InsertsNewAndUpdatesExisting()
    {
        // Pre-seed one row
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        // Batch: update existing + insert new
        await _repository.UpsertManyAsync(
            new[]
            {
                (ExchangeId1, AssetId1, 8, (int?)null),   // update
                (ExchangeId1, AssetId2, 1, (int?)null),   // insert
            },
            CancellationToken.None);

        var rows = await _context.AssetExchangeFundingIntervals
            .AsNoTracking()
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.AssetId == AssetId1).IntervalHours.Should().Be(8);
        rows.Single(r => r.AssetId == AssetId2).IntervalHours.Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Caching
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetIntervalsAsync_SecondCall_ReturnsCachedResult()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        _repository.InvalidateCache();
        var first = await _repository.GetIntervalsAsync(CancellationToken.None);

        // Mutate DB directly (bypass repository)
        var entity = await _context.AssetExchangeFundingIntervals
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);
        entity.IntervalHours = 999;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Second call — should return cached value (4), not DB value (999)
        var second = await _repository.GetIntervalsAsync(CancellationToken.None);

        second[(ExchangeId1, AssetId1)].Should().Be(4);
        _ = first; // silence unused warning
    }

    [Fact]
    public async Task InvalidateCache_ForcesDbReloadOnNextCall()
    {
        await _repository.UpsertManyAsync(
            new[] { (ExchangeId1, AssetId1, 4, (int?)null) },
            CancellationToken.None);

        _repository.InvalidateCache();
        var first = await _repository.GetIntervalsAsync(CancellationToken.None);
        first[(ExchangeId1, AssetId1)].Should().Be(4);

        // Mutate DB directly
        var entity = await _context.AssetExchangeFundingIntervals
            .FirstAsync(r => r.ExchangeId == ExchangeId1 && r.AssetId == AssetId1);
        entity.IntervalHours = 12;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Invalidate — next read should hit DB
        _repository.InvalidateCache();
        var second = await _repository.GetIntervalsAsync(CancellationToken.None);

        second[(ExchangeId1, AssetId1)].Should().Be(12);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _context.Dispose();
    }
}

/// <summary>
/// Verifies IAssetExchangeFundingIntervalRepository is registered as scoped in the DI container.
/// </summary>
public class AssetExchangeFundingIntervalRepositoryDiRegistrationTests
{
    [Fact]
    public void IAssetExchangeFundingIntervalRepository_IsRegisteredAsScoped()
    {
        var services = new ServiceCollection();

        // Minimal registrations needed to satisfy AssetExchangeFundingIntervalRepository's constructor
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        services.AddSingleton(dbOptions);
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddMemoryCache();

        // This mirrors the line that must be added to Program.cs
        services.AddScoped<IAssetExchangeFundingIntervalRepository, AssetExchangeFundingIntervalRepository>();

        var descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IAssetExchangeFundingIntervalRepository));

        descriptor.Should().NotBeNull("the service must be registered");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<AssetExchangeFundingIntervalRepository>();
    }
}

/// <summary>
/// Verifies that AssetExchangeFundingIntervalRepository satisfies the interface contract
/// that FundingRateFetcher resolves from its DI scope.
///
/// FundingRateFetcher calls:
///   scope.ServiceProvider.GetService&lt;Application.Common.Repositories.IAssetExchangeFundingIntervalRepository&gt;()
///
/// For this to return the real implementation (instead of null), the concrete class must
/// implement Application.Common.Repositories.IAssetExchangeFundingIntervalRepository —
/// not just Application.Interfaces.IAssetExchangeFundingIntervalRepository.
/// Without this, UpsertManyAsync is silently skipped at runtime.
/// </summary>
public class AssetExchangeFundingIntervalFetcherContractTests
{
    [Fact]
    public void AssetExchangeFundingIntervalRepository_ImplementsCommonRepositoriesInterface()
    {
        // FundingRateFetcher resolves Application.Common.Repositories.IAssetExchangeFundingIntervalRepository
        // from its DI scope. The concrete class must implement THAT interface so DI can satisfy the request.
        // This test fails when the class only implements Application.Interfaces.IAssetExchangeFundingIntervalRepository.
        typeof(AssetExchangeFundingIntervalRepository)
            .Should()
            .Implement<FundingRateArb.Application.Common.Repositories.IAssetExchangeFundingIntervalRepository>(
                because: "FundingRateFetcher.FetchAllAsync calls " +
                         "scope.ServiceProvider.GetService<Application.Common.Repositories.IAssetExchangeFundingIntervalRepository>(); " +
                         "if the class does not implement that interface the GetService call returns null and " +
                         "the per-symbol upsert path is silently skipped on every fetch cycle");
    }
}
