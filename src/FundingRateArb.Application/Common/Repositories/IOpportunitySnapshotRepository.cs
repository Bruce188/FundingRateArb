using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IOpportunitySnapshotRepository
{
    Task AddRangeAsync(IEnumerable<OpportunitySnapshot> snapshots, CancellationToken ct = default);
    Task<List<OpportunitySnapshot>> GetRecentAsync(DateTime from, DateTime to, int skip = 0, int take = 200, CancellationToken ct = default);
    Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>Returns aggregate skip reason stats for the time range using SQL GROUP BY.
    /// Also returns total count and opened count for summary stats.</summary>
    Task<(int TotalCount, int OpenedCount, Dictionary<string, int> SkipReasons)> GetSkipReasonStatsAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
