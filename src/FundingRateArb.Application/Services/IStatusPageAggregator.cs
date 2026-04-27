using System.Threading;
using System.Threading.Tasks;
using FundingRateArb.Application.ViewModels;

namespace FundingRateArb.Application.Services;

/// <summary>
/// Cache wrapper for the Status page aggregate. Single composite cache key
/// (<c>"admin:status:viewmodel"</c>) with 60s AbsoluteExpirationRelativeToNow.
/// Implementer fans out per-section queries via <c>IServiceScopeFactory.CreateScope()</c>
/// to avoid DbContext thread-affinity issues on parallel reads.
/// </summary>
public interface IStatusPageAggregator
{
    Task<StatusViewModel> GetAsync(CancellationToken ct);
}
