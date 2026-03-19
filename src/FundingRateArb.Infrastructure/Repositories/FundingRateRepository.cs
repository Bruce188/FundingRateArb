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
        // Correlated subquery: fetch the row whose RecordedAt matches the max per (Exchange, Asset).
        // More efficient than GroupBy + First() which can cause client-side evaluation on large tables.
        return await _context.FundingRateSnapshots
            .Include(f => f.Exchange)
            .Include(f => f.Asset)
            .AsNoTracking()
            .Where(f => f.RecordedAt == _context.FundingRateSnapshots
                .Where(x => x.ExchangeId == f.ExchangeId && x.AssetId == f.AssetId)
                .Max(x => (DateTime?)x.RecordedAt))
            .ToListAsync();
    }

    public Task<List<FundingRateSnapshot>> GetHistoryAsync(int assetId, int exchangeId, DateTime from, DateTime to) =>
        _context.FundingRateSnapshots
            .Where(f => f.AssetId == assetId && f.ExchangeId == exchangeId
                        && f.RecordedAt >= from && f.RecordedAt <= to)
            .OrderBy(f => f.RecordedAt)
            .ToListAsync();

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
}
