using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IFundingRateRepository
{
    Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync();
    Task<List<FundingRateSnapshot>> GetHistoryAsync(int assetId, int exchangeId, DateTime from, DateTime to, int take = 1000, int skip = 0);
    void Add(FundingRateSnapshot snapshot);
    void AddRange(IEnumerable<FundingRateSnapshot> snapshots);
    Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);

    // Hourly aggregate methods (30-day retention)
    Task<List<FundingRateHourlyAggregate>> GetHourlyAggregatesAsync(
        int? assetId, int? exchangeId, DateTime from, DateTime to, CancellationToken ct = default);
    void AddAggregateRange(IEnumerable<FundingRateHourlyAggregate> aggregates);
    Task<int> PurgeAggregatesOlderThanAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>
    /// Returns raw snapshots within a time range for aggregation.
    /// </summary>
    Task<List<FundingRateSnapshot>> GetSnapshotsInRangeAsync(
        DateTime from, DateTime to, CancellationToken ct = default);

    // Analytics query methods
    Task<List<FundingRateHourlyAggregate>> GetLatestAggregatePerAssetExchangeAsync(CancellationToken ct = default);
}
