using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IReconciliationReportRepository
{
    /// <summary>Returns the most recent reconciliation report by <c>RunAtUtc</c> descending.
    /// Returns null when no rows exist (e.g. fresh deployment before first tick).</summary>
    Task<ReconciliationReport?> GetMostRecentAsync(CancellationToken ct = default);

    /// <summary>Adds a new report to the change tracker. Persistence happens via the
    /// containing <see cref="IUnitOfWork.SaveAsync"/> call.</summary>
    void Add(ReconciliationReport report);
}
