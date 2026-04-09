using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Repositories;

public class FundingRateRepository : IFundingRateRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<FundingRateRepository>? _logger;

    public FundingRateRepository(AppDbContext context, ILogger<FundingRateRepository>? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    // plan-v60 Task 3.2: SQL error numbers that indicate transient login-phase or
    // connectivity failures worth surfacing as a degraded state instead of a hard 500.
    // Mirrors the DbContext EnableRetryOnFailure allowlist.
    private static readonly HashSet<int> TransientLoginFailureErrorCodes =
    [
        -2,      // timeout
        35,      // network path not found
        64,      // connection forcibly closed
        233,     // pre-login handshake
        10053,   // connection aborted
        10054,   // connection reset
        10060,   // connection timed out
        10928,   // Azure SQL: resource limit reached
        10929,   // Azure SQL: too many sessions
        40197,   // Azure SQL: service encountered an error
        40501,   // Azure SQL: service is busy
        40613,   // Azure SQL: database unavailable
    ];

    private static bool IsTransientLoginFailure(SqlException ex)
    {
        if (TransientLoginFailureErrorCodes.Contains(ex.Number))
        {
            return true;
        }
        return ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync()
    {
        // H5: Replace O(N^2) correlated subquery with GroupBy + Max + Join for O(N log N) performance.
        // The correlated subquery re-scans the entire table per row; with 3.6M rows this is the bottleneck.
        var maxTimes = _context.FundingRateSnapshots
            .GroupBy(f => new { f.ExchangeId, f.AssetId })
            .Select(g => new { g.Key.ExchangeId, g.Key.AssetId, MaxAt = g.Max(x => x.RecordedAt) });

        try
        {
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
        catch (SqlException ex) when (IsTransientLoginFailure(ex))
        {
            // plan-v60 Task 3.2: surface transient SQL outages as a domain exception
            // so the Application layer (SignalEngine) can return a degraded result
            // without taking a dependency on Microsoft.Data.SqlClient.
            _logger?.LogWarning(ex,
                "SQL transient login-phase failure in GetLatestPerExchangePerAssetAsync (Number={ErrorNumber}); surfacing as DatabaseUnavailable",
                ex.Number);
            throw new DatabaseUnavailableException(
                "Database temporarily unavailable during funding rate read.", ex);
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
