using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class PositionRepository : IPositionRepository
{
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

        // B1: Apply row limit in SQL (TOP/LIMIT) to prevent unbounded memory usage
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

        // Compute scalar KPIs via SQL GROUP BY. TotalHoldHours is computed
        // client-side since EF.Functions.DateDiffSecond is not available on all providers.
        var scalarResult = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalTrades = g.Count(),
                WinCount = g.Count(p => p.RealizedPnl > 0),
                TotalPnl = g.Sum(p => p.RealizedPnl!.Value),
                Pnl7d = g.Where(p => p.ClosedAt >= cutoff7d).Sum(p => p.RealizedPnl!.Value),
                Pnl30d = g.Where(p => p.ClosedAt >= cutoff30d).Sum(p => p.RealizedPnl!.Value),
                BestPnl = g.Max(p => p.RealizedPnl!.Value),
                WorstPnl = g.Min(p => p.RealizedPnl!.Value),
            })
            .FirstOrDefaultAsync(ct);

        if (scalarResult is null)
        {
            return new KpiAggregateDto();
        }

        // Compute total hold hours from a lightweight projection (only OpenedAt + ClosedAt)
        var holdData = await query
            .Select(p => new { p.OpenedAt, p.ClosedAt })
            .ToListAsync(ct);
        var totalHoldHours = holdData.Sum(p => (p.ClosedAt - p.OpenedAt)?.TotalHours ?? 0);

        var result = new KpiAggregateDto
        {
            TotalTrades = scalarResult.TotalTrades,
            WinCount = scalarResult.WinCount,
            TotalPnl = scalarResult.TotalPnl,
            Pnl7d = scalarResult.Pnl7d,
            Pnl30d = scalarResult.Pnl30d,
            BestPnl = scalarResult.BestPnl,
            WorstPnl = scalarResult.WorstPnl,
            TotalHoldHours = totalHoldHours,
        };

        return result ?? new KpiAggregateDto();
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
                TotalPnl = g.Sum(p => p.RealizedPnl!.Value),
            })
            .OrderByDescending(a => a.TotalPnl)
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
                TotalPnl = g.Sum(p => p.RealizedPnl!.Value),
            })
            .OrderByDescending(e => e.TotalPnl)
            .ToListAsync(ct);
    }

    public void Add(ArbitragePosition position) =>
        _context.ArbitragePositions.Add(position);

    public void Update(ArbitragePosition position) =>
        _context.ArbitragePositions.Update(position);
}
