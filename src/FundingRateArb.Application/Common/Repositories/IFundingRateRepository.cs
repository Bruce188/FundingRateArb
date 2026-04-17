using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IFundingRateRepository
{
    Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync();
    Task<List<FundingRateSnapshot>> GetHistoryAsync(int assetId, int exchangeId, DateTime from, DateTime to, int take = 1000, int skip = 0);
    void Add(FundingRateSnapshot snapshot);
    void AddRange(IEnumerable<FundingRateSnapshot> snapshots);
    /// <param name="force">When true AND the internal suppression counter > 0, bypasses the 15-minute cooldown window for this invocation only. Default false.</param>
    Task<int> PurgeOlderThanAsync(DateTime cutoff, bool force = false, CancellationToken ct = default);

    /// <summary>Returns the current count of purge invocations suppressed by cooldown since the last successful run.</summary>
    int GetSuppressedPurgeCount();

    // Hourly aggregate methods (30-day retention)
    Task<List<FundingRateHourlyAggregate>> GetHourlyAggregatesAsync(
        int? assetId, int? exchangeId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<bool> HourlyAggregatesExistAsync(DateTime from, DateTime to, CancellationToken ct = default);
    void AddAggregateRange(IEnumerable<FundingRateHourlyAggregate> aggregates);
    Task<int> PurgeAggregatesOlderThanAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>
    /// Returns raw snapshots within a time range for aggregation.
    /// </summary>
    Task<List<FundingRateSnapshot>> GetSnapshotsInRangeAsync(
        DateTime from, DateTime to, CancellationToken ct = default);

    // Analytics query methods
    Task<List<FundingRateHourlyAggregate>> GetLatestAggregatePerAssetExchangeAsync(CancellationToken ct = default);

    /// <summary>
    /// Computes mean and standard deviation of AvgRatePerHour per (AssetId, ExchangeId) pair via SQL aggregation.
    /// </summary>
    Task<List<(int AssetId, int ExchangeId, decimal Mean, decimal StdDev)>> GetAggregateStatsByPairAsync(
        DateTime from, DateTime to, CancellationToken ct = default);
}
