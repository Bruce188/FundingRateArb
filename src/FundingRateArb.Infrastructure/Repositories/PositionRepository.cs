using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class PositionRepository : IPositionRepository
{
    /// <summary>Maximum number of grouped results returned by per-asset/per-exchange KPI queries.</summary>
    internal const int MaxGroupResults = 100;

    /// <summary>Maximum number of rows fetched for hold-time computation. Caps memory usage for large date windows.</summary>
    private const int MaxHoldDataRows = 10_000;

    private readonly AppDbContext _context;

    public PositionRepository(AppDbContext context) => _context = context;

    public Task<ArbitragePosition?> GetByIdAsync(int id) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .FirstOrDefaultAsync(p => p.Id == id);

    public Task<List<ArbitragePosition>> GetOpenAsync() =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Open)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetOpenByUserAsync(string userId) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Open && p.UserId == userId)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetOpenTrackedAsync() =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => p.Status == PositionStatus.Open)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetByUserAsync(string userId, int skip = 0, int take = 500) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.OpenedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetAllAsync(int skip = 0, int take = 500) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .OrderByDescending(p => p.OpenedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetClosedSinceAsync(DateTime since) =>
        _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed && p.ClosedAt >= since)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetClosedWithNavigationSinceAsync(DateTime since, string? userId = null, int maxRows = 10_000, CancellationToken ct = default)
    {
        var query = _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed && p.ClosedAt >= since);

        if (userId is not null)
        {
            query = query.Where(p => p.UserId == userId);
        }

        // Apply row limit in SQL (TOP/LIMIT) to prevent unbounded memory usage
        return query
            .OrderByDescending(p => p.ClosedAt)
            .Take(maxRows)
            .ToListAsync(ct);
    }

    public Task<List<ClosedPositionKpiDto>> GetClosedKpiProjectionSinceAsync(DateTime since, string? userId = null, int maxRows = 10_000, CancellationToken ct = default)
    {
        var query = _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed && p.ClosedAt >= since);

        if (userId is not null)
        {
            query = query.Where(p => p.UserId == userId);
        }

        return query
            .OrderByDescending(p => p.ClosedAt)
            .Take(maxRows)
            .Select(p => new ClosedPositionKpiDto
            {
                RealizedPnl = p.RealizedPnl,
                ClosedAt = p.ClosedAt,
                OpenedAt = p.OpenedAt,
                AssetSymbol = p.Asset != null ? p.Asset.Symbol : "Unknown",
                LongExchangeName = p.LongExchange != null ? p.LongExchange.Name : "?",
                ShortExchangeName = p.ShortExchange != null ? p.ShortExchange.Name : "?",
            })
            .ToListAsync(ct);
    }

    public Task<List<ArbitragePosition>> GetByStatusAsync(PositionStatus status) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => p.Status == status)
            .ToListAsync();

    public Task<int> CountByStatusAsync(PositionStatus status) =>
        _context.ArbitragePositions
            .CountAsync(p => p.Status == status);

    public Task<int> CountByStatusesAsync(params PositionStatus[] statuses) =>
        _context.ArbitragePositions
            .CountAsync(p => statuses.Contains(p.Status));

    public Task<List<ArbitragePosition>> GetByStatusesAsync(params PositionStatus[] statuses) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => statuses.Contains(p.Status))
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetByUserAndStatusesAsync(string userId, params PositionStatus[] statuses) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => p.UserId == userId && statuses.Contains(p.Status))
            .ToListAsync();

    public async Task<KpiAggregateDto> GetKpiAggregatesAsync(DateTime since, string? userId = null, CancellationToken ct = default)
    {
        var query = _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed && p.ClosedAt >= since && p.RealizedPnl != null);

        if (userId is not null)
        {
            query = query.Where(p => p.UserId == userId);
        }

        var now = DateTime.UtcNow;
        var cutoff7d = now.AddDays(-7);
        var cutoff30d = now.AddDays(-30);

        // Compute scalar KPIs via SQL GROUP BY — single round-trip for all aggregates.
        // Hold-hours computed from a lightweight two-column projection (second query) because
        // EF.Functions.DateDiffSecond is SQL Server-only and incompatible with InMemory test provider.
        var scalarResult = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalTrades = g.Count(),
                WinCount = g.Count(p => p.RealizedPnl > 0),
                TotalPnl = g.Sum(p => (decimal?)p.RealizedPnl) ?? 0m,
                Pnl7d = g.Where(p => p.ClosedAt >= cutoff7d).Sum(p => (decimal?)p.RealizedPnl) ?? 0m,
                Pnl30d = g.Where(p => p.ClosedAt >= cutoff30d).Sum(p => (decimal?)p.RealizedPnl) ?? 0m,
                BestPnl = g.Max(p => (decimal?)p.RealizedPnl) ?? 0m,
                WorstPnl = g.Min(p => (decimal?)p.RealizedPnl) ?? 0m,
            })
            .FirstOrDefaultAsync(ct);

        if (scalarResult is null)
        {
            return new KpiAggregateDto();
        }

        // Lightweight projection for hold-hours — only 2 columns, no full entity materialization.
        // Safety cap prevents unbounded memory usage for large date windows.
        // Computes client-side since DateTime subtraction doesn't translate to all EF providers.
        // At scale, a composite index on (Status, ClosedAt) INCLUDE (OpenedAt, RealizedPnl) benefits this query.
        var holdData = await query
            .OrderByDescending(p => p.ClosedAt)
            .Take(MaxHoldDataRows)
            .Select(p => new { p.OpenedAt, p.ClosedAt })
            .ToListAsync(ct);
        var totalHoldHours = holdData.Sum(p => (p.ClosedAt - p.OpenedAt)?.TotalHours ?? 0);

        return new KpiAggregateDto
        {
            TotalTrades = scalarResult.TotalTrades,
            WinCount = scalarResult.WinCount,
            TotalPnl = scalarResult.TotalPnl,
            Pnl7d = scalarResult.Pnl7d,
            Pnl30d = scalarResult.Pnl30d,
            BestPnl = scalarResult.BestPnl,
            WorstPnl = scalarResult.WorstPnl,
            TotalHoldHours = totalHoldHours,
            HoldDataCount = holdData.Count,
        };
    }

    public Task<List<AssetKpiAggregateDto>> GetPerAssetKpiAsync(DateTime since, string? userId = null, CancellationToken ct = default)
    {
        var query = _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed && p.ClosedAt >= since && p.RealizedPnl != null);

        if (userId is not null)
        {
            query = query.Where(p => p.UserId == userId);
        }

        return query
            .GroupBy(p => p.Asset != null ? p.Asset.Symbol : "Unknown")
            .Select(g => new AssetKpiAggregateDto
            {
                AssetSymbol = g.Key,
                Trades = g.Count(),
                WinCount = g.Count(p => p.RealizedPnl > 0),
                TotalPnl = g.Sum(p => (decimal?)p.RealizedPnl) ?? 0m,
            })
            .OrderByDescending(a => a.TotalPnl)
            .Take(MaxGroupResults)
            .ToListAsync(ct);
    }

    public Task<List<ExchangePairKpiAggregateDto>> GetPerExchangePairKpiAsync(DateTime since, string? userId = null, CancellationToken ct = default)
    {
        var query = _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed && p.ClosedAt >= since && p.RealizedPnl != null);

        if (userId is not null)
        {
            query = query.Where(p => p.UserId == userId);
        }

        return query
            .GroupBy(p => new
            {
                Long = p.LongExchange != null ? p.LongExchange.Name : "?",
                Short = p.ShortExchange != null ? p.ShortExchange.Name : "?",
            })
            .Select(g => new ExchangePairKpiAggregateDto
            {
                LongExchangeName = g.Key.Long,
                ShortExchangeName = g.Key.Short,
                Trades = g.Count(),
                WinCount = g.Count(p => p.RealizedPnl > 0),
                TotalPnl = g.Sum(p => (decimal?)p.RealizedPnl) ?? 0m,
            })
            .OrderByDescending(e => e.TotalPnl)
            .Take(MaxGroupResults)
            .ToListAsync(ct);
    }

    public async Task<SlippageRollupDto> GetSlippageRollupAsync(TimeSpan window, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - window;

        var byPair = await _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.OpenedAt >= since && p.ClosedAt != null)
            .GroupBy(p => new
            {
                LongExchangeName = p.LongExchange != null ? p.LongExchange.Name : "?",
                ShortExchangeName = p.ShortExchange != null ? p.ShortExchange.Name : "?",
            })
            .Select(g => new PairSlippageRollupDto
            {
                LongExchangeName = g.Key.LongExchangeName,
                ShortExchangeName = g.Key.ShortExchangeName,
                PositionCount = g.Count(),
                AvgLongEntrySlippagePct = g.Average(p => p.LongEntrySlippagePct),
                AvgShortEntrySlippagePct = g.Average(p => p.ShortEntrySlippagePct),
                AvgLongExitSlippagePct = g.Average(p => p.LongExitSlippagePct),
                AvgShortExitSlippagePct = g.Average(p => p.ShortExitSlippagePct),
            })
            .Take(MaxGroupResults)
            .ToListAsync(ct);

        var byAsset = await _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.OpenedAt >= since && p.ClosedAt != null)
            .GroupBy(p => p.Asset != null ? p.Asset.Symbol : "Unknown")
            .Select(g => new AssetSlippageRollupDto
            {
                AssetSymbol = g.Key,
                PositionCount = g.Count(),
                AvgLongEntrySlippagePct = g.Average(p => p.LongEntrySlippagePct),
                AvgShortEntrySlippagePct = g.Average(p => p.ShortEntrySlippagePct),
                AvgLongExitSlippagePct = g.Average(p => p.LongExitSlippagePct),
                AvgShortExitSlippagePct = g.Average(p => p.ShortExitSlippagePct),
            })
            .Take(MaxGroupResults)
            .ToListAsync(ct);

        return new SlippageRollupDto { ByPair = byPair, ByAsset = byAsset };
    }

    public Task<decimal> SumRealizedPnlExcludingPhantomAsync(string userId, params PositionStatus[] statuses) =>
        _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.UserId == userId && statuses.Contains(p.Status) && !p.IsPhantomFeeBackfill)
            .SumAsync(p => p.RealizedPnl ?? 0m);

    public async Task<IReadOnlyList<ArbitragePosition>> GetPendingConfirmAsync(
        string userId, TimeSpan olderThan, CancellationToken ct, int maxResults = 100)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        return await _context.ArbitragePositions
            .AsNoTracking()
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => p.UserId == userId
                     && p.Status == PositionStatus.Opening
                     && p.OpenConfirmedAt == null
                     && p.OpenedAt < cutoff)
            .OrderBy(p => p.OpenedAt)
            .Take(maxResults)
            .ToListAsync(ct);
    }

    public Task<int> CountPhantomFeeRowsSinceAsync(DateTime since, CancellationToken ct = default)
    {
        return _context.ArbitragePositions
            .Where(p => p.Status == PositionStatus.Failed
                        && (p.LongOrderId == null || p.LongOrderId == "")
                        && (p.ShortOrderId == null || p.ShortOrderId == "")
                        && (p.EntryFeesUsdc + p.ExitFeesUsdc) > 0
                        && p.ClosingStartedAt != null
                        && p.ClosingStartedAt >= since)
            .CountAsync(ct);
    }

    public async Task<List<PnlAttributionWindowDto>> GetPnlAttributionWindowsAsync(
        IReadOnlyList<DateTime> sinceUtc, CancellationToken ct = default)
    {
        var result = new List<PnlAttributionWindowDto>();
        var now = DateTime.UtcNow;

        foreach (var since in sinceUtc)
        {
            var isLifetime = since == DateTime.MinValue;

            var query = _context.ArbitragePositions
                .AsNoTracking()
                .Where(p => p.Status == PositionStatus.Closed || p.Status == PositionStatus.EmergencyClosed);

            if (!isLifetime)
            {
                query = query.Where(p => p.OpenedAt >= since);
            }

            var agg = await query
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    GrossFunding = g.Sum(p => p.AccumulatedFunding),
                    EntryFees = g.Sum(p => p.EntryFeesUsdc),
                    ExitFees = g.Sum(p => p.ExitFeesUsdc),
                    SlippageResidual = g.Sum(p => p.AccumulatedFunding - p.EntryFeesUsdc - p.ExitFeesUsdc - (p.RealizedPnl ?? 0m)),
                    NetRealized = g.Sum(p => (decimal?)p.RealizedPnl) ?? 0m,
                })
                .FirstOrDefaultAsync(ct);

            string window;
            if (isLifetime)
            {
                window = "Lifetime";
            }
            else
            {
                var days = (now - since).TotalDays;
                window = days is >= 6 and <= 8 ? "7d"
                    : days is >= 29 and <= 31 ? "30d"
                    : $"{(int)Math.Round(days)}d";
            }

            result.Add(new PnlAttributionWindowDto
            {
                Window = window,
                GrossFunding = agg?.GrossFunding ?? 0m,
                EntryFees = agg?.EntryFees ?? 0m,
                ExitFees = agg?.ExitFees ?? 0m,
                SlippageResidual = agg?.SlippageResidual ?? 0m,
                NetRealized = agg?.NetRealized ?? 0m,
            });
        }

        return result;
    }

    public async Task<List<HoldTimeBucketDto>> GetHoldTimeBucketsAsync(CancellationToken ct = default)
    {
        // EF.Functions.DateDiffSecond is SQL Server-only; use client-side bucketing
        // (same pattern as GetKpiAsync hold-hours calculation) — safe at current scale.
        var rows = await _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => (p.Status == PositionStatus.Closed || p.Status == PositionStatus.EmergencyClosed)
                        && p.ClosedAt != null)
            .Select(p => new { p.OpenedAt, ClosedAt = p.ClosedAt!.Value, p.RealizedPnl })
            .ToListAsync(ct);

        return rows
            .Select(p =>
            {
                var holdSeconds = (p.ClosedAt - p.OpenedAt).TotalSeconds;
                var bucket = holdSeconds < 60 ? "<60s"
                    : holdSeconds < 300 ? "<5m"
                    : holdSeconds < 3600 ? "<1h"
                    : holdSeconds < 21600 ? "<6h"
                    : ">=6h";
                return new { Bucket = bucket, IsWin = p.RealizedPnl > 0, Pnl = p.RealizedPnl ?? 0m };
            })
            .GroupBy(x => x.Bucket)
            .Select(g => new HoldTimeBucketDto
            {
                Bucket = g.Key,
                Count = g.Count(),
                WinCount = g.Count(x => x.IsWin),
                TotalPnl = g.Sum(x => x.Pnl),
            })
            .ToList();
    }

    public Task<int> CountEmergencyClosedZeroFillSinceAsync(DateTime since, CancellationToken ct = default)
    {
        return _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.EmergencyClosed
                && p.LongFilledQuantity == 0m
                && p.ShortFilledQuantity == 0m
                && p.OpenedAt >= since)
            .CountAsync(ct);
    }

    public async Task<List<FailedOpenEventDto>> GetRecentFailedOpensAsync(DateTime since, CancellationToken ct = default)
    {
        var assets = await _context.Assets
            .AsNoTracking()
            .ToDictionaryAsync(a => a.Id, a => a.Symbol, ct);

        var exchanges = await _context.Exchanges
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, ct);

        var grouped = await _context.ArbitragePositions
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Failed && p.OpenedAt >= since)
            .GroupBy(p => new { p.AssetId, p.LongExchangeId, p.ShortExchangeId })
            .Select(g => new
            {
                g.Key.AssetId,
                g.Key.LongExchangeId,
                g.Key.ShortExchangeId,
                Count = g.Count(),
                LatestAt = g.Max(p => p.OpenedAt),
            })
            .ToListAsync(ct);

        return grouped
            .Select(g => new FailedOpenEventDto
            {
                AssetSymbol = assets.TryGetValue(g.AssetId, out var sym) ? sym : $"#{g.AssetId}",
                LongExchangeName = exchanges.TryGetValue(g.LongExchangeId, out var ln) ? ln : $"#{g.LongExchangeId}",
                ShortExchangeName = exchanges.TryGetValue(g.ShortExchangeId, out var sn) ? sn : $"#{g.ShortExchangeId}",
                Count = g.Count,
                LatestAt = g.LatestAt,
            })
            .OrderByDescending(x => x.LatestAt)
            .ToList();
    }

    public void Add(ArbitragePosition position) =>
        _context.ArbitragePositions.Add(position);

    public void Update(ArbitragePosition position) =>
        _context.ArbitragePositions.Update(position);
}
