using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class PairExecutionStatsRepository : IPairExecutionStatsRepository
{
    private readonly AppDbContext _context;

    public PairExecutionStatsRepository(AppDbContext context) => _context = context;

    public Task<List<PairExecutionStats>> GetAllAsync(CancellationToken ct = default) =>
        _context.PairExecutionStats
            .AsNoTracking()
            .OrderByDescending(p => p.LastUpdatedAt)
            .ToListAsync(ct);

    public Task<PairExecutionStats?> GetByPairAsync(string longEx, string shortEx, CancellationToken ct = default) =>
        _context.PairExecutionStats
            .Where(p => EF.Functions.Like(p.LongExchangeName, longEx)
                     && EF.Functions.Like(p.ShortExchangeName, shortEx))
            .FirstOrDefaultAsync(ct);

    public async Task UpsertAsync(PairExecutionStats row, CancellationToken ct = default)
    {
        var existing = await GetByPairAsync(row.LongExchangeName, row.ShortExchangeName, ct);
        if (existing is null)
        {
            _context.PairExecutionStats.Add(row);
            return;
        }
        existing.WindowStart = row.WindowStart;
        existing.WindowEnd = row.WindowEnd;
        existing.CloseCount = row.CloseCount;
        existing.WinCount = row.WinCount;
        existing.TotalPnlUsdc = row.TotalPnlUsdc;
        existing.AvgHoldSec = row.AvgHoldSec;
        existing.IsDenied = row.IsDenied;
        existing.DeniedUntil = row.DeniedUntil;
        existing.DeniedReason = row.DeniedReason;
        existing.LastUpdatedAt = row.LastUpdatedAt;
    }

    public async Task<HashSet<(string LongExchangeName, string ShortExchangeName)>> GetCurrentlyDeniedKeysAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await _context.PairExecutionStats
            .AsNoTracking()
            .Where(p => p.IsDenied && (p.DeniedUntil == null || p.DeniedUntil > now))
            .Select(p => new { p.LongExchangeName, p.ShortExchangeName })
            .ToListAsync(ct);
        var set = new HashSet<(string, string)>();
        foreach (var r in rows)
        {
            set.Add((r.LongExchangeName, r.ShortExchangeName));
        }

        return set;
    }
}
