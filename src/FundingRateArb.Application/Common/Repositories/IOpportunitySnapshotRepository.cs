using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IOpportunitySnapshotRepository
{
    Task AddRangeAsync(IEnumerable<OpportunitySnapshot> snapshots, CancellationToken ct = default);
    Task<List<OpportunitySnapshot>> GetRecentAsync(DateTime from, DateTime to, int skip = 0, int take = 200, CancellationToken ct = default);
    Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
