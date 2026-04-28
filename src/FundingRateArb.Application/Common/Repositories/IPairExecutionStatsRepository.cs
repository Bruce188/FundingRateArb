using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IPairExecutionStatsRepository
{
    /// <summary>Returns all rows ordered by <c>LastUpdatedAt</c> descending. Used by the
    /// <c>/Admin/PairDenyList</c> page render.</summary>
    Task<List<PairExecutionStats>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns the single row keyed by <c>(LongExchangeName, ShortExchangeName)</c>, or null.
    /// Match is case-insensitive (<c>StringComparison.OrdinalIgnoreCase</c>) per Risk #8.</summary>
    Task<PairExecutionStats?> GetByPairAsync(string longEx, string shortEx, CancellationToken ct = default);

    /// <summary>Inserts a new row or updates the existing row matching
    /// <c>(LongExchangeName, ShortExchangeName)</c>. Caller is responsible for setting
    /// <c>LastUpdatedAt = DateTime.UtcNow</c>. Does NOT call <c>SaveChangesAsync</c> — caller persists
    /// via the containing <see cref="IUnitOfWork.SaveAsync"/>.</summary>
    Task UpsertAsync(PairExecutionStats row, CancellationToken ct = default);

    /// <summary>Returns the set of <c>(LongExchangeName, ShortExchangeName)</c> tuples currently denied
    /// — <c>IsDenied = true AND (DeniedUntil IS NULL OR DeniedUntil &gt; NOW)</c>. Backs the
    /// <c>IPairDenyListSnapshot</c> refresh. SQL projection only; no row materialization.</summary>
    Task<HashSet<(string LongExchangeName, string ShortExchangeName)>> GetCurrentlyDeniedKeysAsync(CancellationToken ct = default);
}
