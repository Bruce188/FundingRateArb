using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class OpportunitySnapshotRepository : IOpportunitySnapshotRepository
{
    private readonly AppDbContext _context;

    public OpportunitySnapshotRepository(AppDbContext context) => _context = context;

    public async Task AddRangeAsync(IEnumerable<OpportunitySnapshot> snapshots, CancellationToken ct = default)
    {
        await _context.OpportunitySnapshots.AddRangeAsync(snapshots, ct);
    }

    public async Task<List<OpportunitySnapshot>> GetRecentAsync(
        DateTime from, DateTime to, int skip = 0, int take = 200, CancellationToken ct = default)
    {
        return await _context.OpportunitySnapshots
            .Where(s => s.RecordedAt >= from && s.RecordedAt <= to)
            .Include(s => s.Asset)
            .Include(s => s.LongExchange)
            .Include(s => s.ShortExchange)
            .OrderByDescending(s => s.RecordedAt)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        return await _context.OpportunitySnapshots
            .Where(s => s.RecordedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<(int TotalCount, int OpenedCount, Dictionary<string, int> SkipReasons)> GetSkipReasonStatsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var baseQuery = _context.OpportunitySnapshots
            .Where(s => s.RecordedAt >= from && s.RecordedAt <= to);

        // Combine total and opened counts into a single SQL query (2 round-trips instead of 3)
        var summary = await baseQuery
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Opened = g.Count(s => s.WasOpened) })
            .FirstOrDefaultAsync(ct);

        var totalCount = summary?.Total ?? 0;
        var openedCount = summary?.Opened ?? 0;

        var skipReasons = await baseQuery
            .Where(s => !s.WasOpened && s.SkipReason != null && s.SkipReason != "")
            .GroupBy(s => s.SkipReason!)
            .Select(g => new { Reason = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Reason, x => x.Count, ct);

        return (totalCount, openedCount, skipReasons);
    }
}
