using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IFundingRateRepository
{
    Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync();
    Task<List<FundingRateSnapshot>> GetHistoryAsync(int assetId, int exchangeId, DateTime from, DateTime to);
    void Add(FundingRateSnapshot snapshot);
    void AddRange(IEnumerable<FundingRateSnapshot> snapshots);
}
