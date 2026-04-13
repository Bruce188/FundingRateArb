using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Repositories;

public class FundingRateRepository : IFundingRateRepository
{
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FundingRateRepository>? _logger;

    public FundingRateRepository(AppDbContext context, IMemoryCache cache, ILogger<FundingRateRepository>? logger = null)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    // NB2 from review-v131: rely exclusively on SqlTransientErrorNumbers.Contains —
    // the previous ex.Message.Contains("login") heuristic could false-match non-transient
    // authz errors ("login not permitted") and keep the service in degraded mode.
    private static bool IsTransientLoginFailure(SqlException ex) =>
        SqlTransientErrorNumbers.Contains(ex.Number);

    // Cap the degraded path so the banner renders promptly
    // instead of waiting up to 150s for EF's 5-retry × 30s-delay policy to exhaust.
    private static readonly TimeSpan DegradedReadTimeout = TimeSpan.FromSeconds(20);

    private const string LatestRatesCacheKey = "FundingRates:LatestPerExchangePerAsset";
    private static readonly TimeSpan LatestRatesCacheTtl = TimeSpan.FromSeconds(5);

    public async Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync()
    {
        // Return cached result if still fresh (prevents thundering-herd from concurrent callers)
        if (_cache.TryGetValue(LatestRatesCacheKey, out List<FundingRateSnapshot>? cached) && cached is not null)
        {
            return cached;
        }

        // H5: Replace O(N^2) correlated subquery with GroupBy + Max + Join for O(N log N) performance.
        // The correlated subquery re-scans the entire table per row; with 3.6M rows this is the bottleneck.
        var maxTimes = _context.FundingRateSnapshots
            .GroupBy(f => new { f.ExchangeId, f.AssetId })
            .Select(g => new { g.Key.ExchangeId, g.Key.AssetId, MaxAt = g.Max(x => x.RecordedAt) });

        using var timeoutCts = new CancellationTokenSource(DegradedReadTimeout);

        try
        {
            var result = await _context.FundingRateSnapshots
                .Join(maxTimes,
                    f => new { f.ExchangeId, f.AssetId, f.RecordedAt },
                    m => new { m.ExchangeId, m.AssetId, RecordedAt = m.MaxAt },
                    (f, _) => f)
                .Include(f => f.Exchange)
                .Include(f => f.Asset)
                .AsNoTracking()
                .ToListAsync(timeoutCts.Token);

            _cache.Set(LatestRatesCacheKey, result, LatestRatesCacheTtl);
            return result;
        }
        catch (SqlException ex) when (IsTransientLoginFailure(ex))
        {
            // Re-throw as a domain exception so Application-layer callers (SignalEngine)
            // can return a degraded result without referencing Microsoft.Data.SqlClient.
            // NB2: log only the structural metadata — the full SqlException.Message
            // commonly contains server name + username on login-phase failures.
            _logger?.LogWarning(
                "SQL transient login-phase failure in GetLatestPerExchangePerAssetAsync Number={ErrorNumber} Type={ExType}",
                ex.Number, ex.GetType().Name);
            throw new DatabaseUnavailableException(
                "Database temporarily unavailable during funding rate read.", ex);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "GetLatestPerExchangePerAssetAsync timed out after {TimeoutSeconds}s; surfacing as DatabaseUnavailable",
                DegradedReadTimeout.TotalSeconds);
            throw new DatabaseUnavailableException(
                $"Database read exceeded {DegradedReadTimeout.TotalSeconds:0}s timeout.");
        }
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
        {
            query = query.Where(a => a.AssetId == assetId.Value);
        }

        if (exchangeId.HasValue)
        {
            query = query.Where(a => a.ExchangeId == exchangeId.Value);
        }

        return await query
            .OrderBy(a => a.HourUtc)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public Task<bool> HourlyAggregatesExistAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        return _context.FundingRateHourlyAggregates
            .AnyAsync(a => a.HourUtc >= from && a.HourUtc < to, ct);
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

    public async Task<List<(int AssetId, int ExchangeId, decimal Mean, decimal StdDev)>> GetAggregateStatsByPairAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var stats = await _context.FundingRateHourlyAggregates
            .Where(a => a.HourUtc >= from && a.HourUtc <= to)
            .GroupBy(a => new { a.AssetId, a.ExchangeId })
            .Where(g => g.Count() >= 2)
            .Select(g => new
            {
                g.Key.AssetId,
                g.Key.ExchangeId,
                Mean = g.Average(a => a.AvgRatePerHour),
                Count = g.Count(),
                SumSquaredDiffs = g.Sum(a =>
                    (a.AvgRatePerHour - g.Average(x => x.AvgRatePerHour))
                    * (a.AvgRatePerHour - g.Average(x => x.AvgRatePerHour)))
            })
            .AsNoTracking()
            .ToListAsync(ct);

        return stats.Select(s =>
        {
            var variance = s.SumSquaredDiffs / (s.Count - 1);
            var stdDev = (decimal)Math.Sqrt((double)variance);
            return (s.AssetId, s.ExchangeId, s.Mean, stdDev);
        }).ToList();
    }
}
