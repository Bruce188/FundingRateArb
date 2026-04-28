using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Tests.Unit.Repositories;

public class PairExecutionStatsRepositoryTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly PairExecutionStatsRepository _repo;

    public PairExecutionStatsRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);
        _repo = new PairExecutionStatsRepository(_context);
    }

    public void Dispose() => _context.Dispose();

    private static PairExecutionStats MakeRow(string longEx = "Hyperliquid", string shortEx = "Aster",
        bool isDenied = false, DateTime? deniedUntil = null, string? deniedReason = null)
        => new()
        {
            LongExchangeName = longEx,
            ShortExchangeName = shortEx,
            WindowStart = DateTime.UtcNow.AddDays(-14),
            WindowEnd = DateTime.UtcNow,
            CloseCount = 5,
            WinCount = 2,
            TotalPnlUsdc = 10m,
            IsDenied = isDenied,
            DeniedUntil = deniedUntil,
            DeniedReason = deniedReason,
            LastUpdatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task Upsert_Insert_NewRow()
    {
        var row = MakeRow();
        await _repo.UpsertAsync(row);
        await _context.SaveChangesAsync();

        var all = await _repo.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].LongExchangeName.Should().Be("Hyperliquid");
    }

    [Fact]
    public async Task Upsert_Update_ExistingRow()
    {
        var row = MakeRow();
        _context.PairExecutionStats.Add(row);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updated = MakeRow();
        updated.WinCount = 9;
        await _repo.UpsertAsync(updated);
        await _context.SaveChangesAsync();

        var all = await _context.PairExecutionStats.AsNoTracking().ToListAsync();
        all.Should().HaveCount(1);
        all[0].WinCount.Should().Be(9);
    }

    [Fact]
    public async Task GetByPair_IsCaseInsensitive()
    {
        var row = MakeRow("Hyperliquid", "Aster");
        _context.PairExecutionStats.Add(row);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // EF In-Memory provider uses LIKE semantics via EF.Functions.Like which is case-insensitive for ASCII
        var found = await _repo.GetByPairAsync("HYPERLIQUID", "Aster");
        found.Should().NotBeNull();
        found!.LongExchangeName.Should().Be("Hyperliquid");
    }

    [Fact]
    public async Task GetCurrentlyDenied_IncludesNullDeniedUntil()
    {
        var row = MakeRow(isDenied: true, deniedUntil: null, deniedReason: "manual: admin");
        _context.PairExecutionStats.Add(row);
        await _context.SaveChangesAsync();

        var keys = await _repo.GetCurrentlyDeniedKeysAsync();
        keys.Should().Contain(("Hyperliquid", "Aster"));
    }

    [Fact]
    public async Task GetCurrentlyDenied_ExcludesExpired()
    {
        var row = MakeRow(isDenied: true, deniedUntil: DateTime.UtcNow.AddHours(-1), deniedReason: "auto: 0-win streak");
        _context.PairExecutionStats.Add(row);
        await _context.SaveChangesAsync();

        var keys = await _repo.GetCurrentlyDeniedKeysAsync();
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCurrentlyDenied_IncludesFutureDeniedUntil()
    {
        var row = MakeRow(isDenied: true, deniedUntil: DateTime.UtcNow.AddHours(1), deniedReason: "auto: 0-win streak");
        _context.PairExecutionStats.Add(row);
        await _context.SaveChangesAsync();

        var keys = await _repo.GetCurrentlyDeniedKeysAsync();
        keys.Should().Contain(("Hyperliquid", "Aster"));
    }

    [Fact]
    public async Task Upsert_Update_ExistingRow_WithMixedCase()
    {
        // Seed with canonical casing
        var seed = MakeRow("Hyperliquid", "Aster");
        _context.PairExecutionStats.Add(seed);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Upsert with mixed-case names — must match the existing row case-insensitively
        var updated = MakeRow("HYPERLIQUID", "aster");
        updated.WinCount = 42;
        await _repo.UpsertAsync(updated);
        await _context.SaveChangesAsync();

        // Exactly one row must exist with the updated WinCount
        var all = await _context.PairExecutionStats.AsNoTracking().ToListAsync();
        all.Should().HaveCount(1);
        all[0].WinCount.Should().Be(42);
    }
}
