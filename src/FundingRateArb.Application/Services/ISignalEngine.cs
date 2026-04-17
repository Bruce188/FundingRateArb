using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface ISignalEngine
{
    Task<List<ArbitrageOpportunityDto>> GetOpportunitiesAsync(CancellationToken ct = default);
    Task<OpportunityResultDto> GetOpportunitiesWithDiagnosticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Pre-warm alias: evaluates all opportunities and primes the opportunity cache.
    /// Delegates to <see cref="GetOpportunitiesWithDiagnosticsAsync"/>.
    /// Called once by <c>FundingRateFetcher</c> after the first successful fetch so that
    /// the first dashboard request skips the cold-start penalty entirely.
    /// </summary>
    Task EvaluateAllAsync(CancellationToken ct = default);
}
