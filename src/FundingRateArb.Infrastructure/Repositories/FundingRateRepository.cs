using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class FundingRateRepository : IFundingRateRepository
{
    private readonly AppDbContext _context;

    public FundingRateRepository(AppDbContext context) => _context = context;

    public async Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync()
    {
        // H5: Replace O(N^2) correlated subquery with GroupBy + Max + Join for O(N log N) performance.
        // The correlated subquery re-scans the entire table per row; with 3.6M rows this is the bottleneck.
        var maxTimes = _context.FundingRateSnapshots
            .GroupBy(f => new { f.ExchangeId, f.AssetId })
            .Select(g => new { g.Key.ExchangeId, g.Key.AssetId, MaxAt = g.Max(x => x.RecordedAt) });

        return await _context.FundingRateSnapshots
            .Join(maxTimes,
                f => new { f.ExchangeId, f.AssetId, f.RecordedAt },
                m => new { m.ExchangeId, m.AssetId, RecordedAt = m.MaxAt },
                (f, _) => f)
            .Include(f => f.Exchange)
            .Include(f => f.Asset)
            .AsNoTracking()
            .ToListAsync();
    }

    public Task<List<FundingRateSnapshot>> GetHistoryAsync(
        int assetId, int exchangeId, DateTime from, DateTime to,
        int take = 1000, int skip = 0)
    {
        // L2: Cap result to avoid unbounded queries on date-range history
        return _context.FundingRateSnapshots
            .Where(f => f.AssetId == assetId && f.ExchangeId == exchangeId
                        && f.RecordedAt >= from && f.RecordedAt <= to)
            .OrderBy(f => f.RecordedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    public void Add(FundingRateSnapshot snapshot) =>
        _context.FundingRateSnapshots.Add(snapshot);

    public void AddRange(IEnumerable<FundingRateSnapshot> snapshots) =>
        _context.FundingRateSnapshots.AddRange(snapshots);

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        return await _context.FundingRateSnapshots
            .Where(s => s.RecordedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    // ── Hourly Aggregate Methods ─────────────────────────────────

    public async Task<List<FundingRateHourlyAggregate>> GetHourlyAggregatesAsync(
        int? assetId, int? exchangeId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var query = _context.FundingRateHourlyAggregates
            .Where(a => a.HourUtc >= from && a.HourUtc <= to);

        if (assetId.HasValue)
            query = query.Where(a => a.AssetId == assetId.Value);
        if (exchangeId.HasValue)
            query = query.Where(a => a.ExchangeId == exchangeId.Value);

        return await query
            .OrderBy(a => a.HourUtc)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public void AddAggregateRange(IEnumerable<FundingRateHourlyAggregate> aggregates) =>
        _context.FundingRateHourlyAggregates.AddRange(aggregates);

    public async Task<int> PurgeAggregatesOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        return await _context.FundingRateHourlyAggregates
            .Where(a => a.HourUtc < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<List<FundingRateSnapshot>> GetSnapshotsInRangeAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await _context.FundingRateSnapshots
            .Where(s => s.RecordedAt >= from && s.RecordedAt < to)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<List<FundingRateHourlyAggregate>> GetLatestAggregatePerAssetExchangeAsync(CancellationToken ct = default)
    {
        // F9: Single GroupBy pass — avoids self-join double-scan.
        // EF Core 8+ translates OrderByDescending + First inside GroupBy to ROW_NUMBER().
        return await _context.FundingRateHourlyAggregates
            .GroupBy(a => new { a.AssetId, a.ExchangeId })
            .Select(g => g.OrderByDescending(a => a.HourUtc).First())
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
