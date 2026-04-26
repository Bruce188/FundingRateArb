using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Infrastructure.Repositories;

public class AssetExchangeFundingIntervalRepository : IAssetExchangeFundingIntervalRepository
{
    private const string CacheKey = "AssetExchangeFundingIntervals";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public AssetExchangeFundingIntervalRepository(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task UpsertManyAsync(
        IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SourceSnapshotId)> entries,
        CancellationToken ct)
    {
        var entriesList = entries.ToList();

        var pairs = entriesList
            .Select(e => (e.ExchangeId, e.AssetId))
            .ToHashSet();

        var existingRows = await _context.AssetExchangeFundingIntervals
            .Where(r => pairs.Select(p => p.ExchangeId).Contains(r.ExchangeId)
                        && pairs.Select(p => p.AssetId).Contains(r.AssetId))
            .ToListAsync(ct);

        // Filter to exact (ExchangeId, AssetId) pairs
        var existingDict = existingRows
            .Where(r => pairs.Contains((r.ExchangeId, r.AssetId)))
            .ToDictionary(r => (r.ExchangeId, r.AssetId));

        var now = DateTime.UtcNow;

        foreach (var (exchangeId, assetId, intervalHours, sourceSnapshotId) in entriesList)
        {
            if (existingDict.TryGetValue((exchangeId, assetId), out var row))
            {
                row.IntervalHours = intervalHours;
                row.UpdatedAtUtc = now;
                row.SourceSnapshotId = sourceSnapshotId;
            }
            else
            {
                _context.AssetExchangeFundingIntervals.Add(new AssetExchangeFundingInterval
                {
                    ExchangeId = exchangeId,
                    AssetId = assetId,
                    IntervalHours = intervalHours,
                    UpdatedAtUtc = now,
                    SourceSnapshotId = sourceSnapshotId,
                });
            }
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<(int ExchangeId, int AssetId), int>> GetIntervalsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<(int, int), int>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _context.AssetExchangeFundingIntervals
            .AsNoTracking()
            .ToListAsync(ct);

        var result = rows.ToDictionary(
            r => (r.ExchangeId, r.AssetId),
            r => r.IntervalHours) as IReadOnlyDictionary<(int, int), int>;

        _cache.Set(CacheKey, result, new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });

        return result!;
    }

    public void InvalidateCache() => _cache.Remove(CacheKey);
}
