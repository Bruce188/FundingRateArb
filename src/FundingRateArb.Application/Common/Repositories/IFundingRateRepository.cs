using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IFundingRateRepository
{
    Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync();
    Task<List<FundingRateSnapshot>> GetHistoryAsync(int assetId, int exchangeId, DateTime from, DateTime to, int take = 1000, int skip = 0);
    void Add(FundingRateSnapshot snapshot);
    void AddRange(IEnumerable<FundingRateSnapshot> snapshots);
    Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
