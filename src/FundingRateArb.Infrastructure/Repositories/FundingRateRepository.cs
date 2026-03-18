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
        // Group by exchange-asset pair, return only the most recent snapshot per pair
        return await _context.FundingRateSnapshots
            .Include(f => f.Exchange)
            .Include(f => f.Asset)
            .GroupBy(f => new { f.ExchangeId, f.AssetId })
            .Select(g => g.OrderByDescending(f => f.RecordedAt).First())
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
}
