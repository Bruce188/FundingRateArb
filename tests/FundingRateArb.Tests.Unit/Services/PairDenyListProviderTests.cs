using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Tests.Unit.Services;

public class PairDenyListProviderTests : IDisposable
{
    private readonly ServiceProvider _rootProvider;
    private readonly AppDbContext _context;
    private readonly PairDenyListProvider _provider;

    public PairDenyListProviderTests()
    {
        var services = new ServiceCollection();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        services.AddSingleton(options);
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<IUnitOfWork>(sp =>
        {
            var ctx = sp.GetRequiredService<AppDbContext>();
            var cache = sp.GetRequiredService<IMemoryCache>();
            return new UnitOfWork(ctx, cache);
        });

        _rootProvider = services.BuildServiceProvider();
        _context = _rootProvider.GetRequiredService<AppDbContext>();
        _provider = new PairDenyListProvider(_rootProvider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose() => _rootProvider.Dispose();

    private static PairExecutionStats MakeDeniedRow(string longEx = "Hyperliquid", string shortEx = "Aster",
        DateTime? deniedUntil = null)
        => new()
        {
            LongExchangeName = longEx,
            ShortExchangeName = shortEx,
            WindowStart = DateTime.UtcNow.AddDays(-14),
            WindowEnd = DateTime.UtcNow,
            IsDenied = true,
            DeniedUntil = deniedUntil,
            DeniedReason = "auto: 0-win streak",
            LastUpdatedAt = DateTime.UtcNow,
        };

    [Fact]
    public void Initial_Snapshot_IsEmpty_AndIsDenied_ReturnsFalse()
    {
        _provider.Current.IsDenied("X", "Y").Should().BeFalse();
        _provider.Current.Count.Should().Be(0);
    }

    [Fact]
    public async Task AfterRefresh_Snapshot_ContainsDeniedKeys()
    {
        _context.PairExecutionStats.Add(MakeDeniedRow());
        await _context.SaveChangesAsync();

        await _provider.RefreshAsync(CancellationToken.None);

        _provider.Current.Count.Should().Be(1);
        _provider.Current.IsDenied("Hyperliquid", "Aster").Should().BeTrue();
    }

    [Fact]
    public async Task IsDenied_IsCaseInsensitive()
    {
        _context.PairExecutionStats.Add(MakeDeniedRow("Hyperliquid", "Aster"));
        await _context.SaveChangesAsync();

        await _provider.RefreshAsync(CancellationToken.None);

        _provider.Current.IsDenied("HYPERLIQUID", "aster").Should().BeTrue();
    }

    [Fact]
    public void IsDenied_NullOrEmpty_ReturnsFalse()
    {
        _provider.Current.IsDenied("", "Aster").Should().BeFalse();
        _provider.Current.IsDenied("Hyperliquid", "").Should().BeFalse();
        // should not throw
    }

    [Fact]
    public async Task Refresh_AtomicSwap_OldReadersSeeOldSnapshot()
    {
        // capture snapshot before any data
        var oldRef = _provider.Current;
        oldRef.Count.Should().Be(0);

        // seed and refresh
        _context.PairExecutionStats.Add(MakeDeniedRow());
        await _context.SaveChangesAsync();
        await _provider.RefreshAsync(CancellationToken.None);

        // old captured reference unchanged (immutable)
        oldRef.Count.Should().Be(0);
        // provider's new current reflects the seed
        _provider.Current.Count.Should().Be(1);
    }
}
