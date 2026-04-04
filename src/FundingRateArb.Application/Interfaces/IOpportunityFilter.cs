using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Interfaces;

/// <summary>
/// Filters arbitrage opportunities per user based on exchange/asset/cooldown/circuit-breaker/threshold criteria.
/// Stateless singleton — all state resides in ICircuitBreakerManager.
/// </summary>
public interface IOpportunityFilter
{
    /// <summary>
    /// Filters global opportunities to a user's enabled exchanges, assets, and threshold.
    /// </summary>
    List<ArbitrageOpportunityDto> FilterUserOpportunities(
        List<ArbitrageOpportunityDto> allOpportunities,
        HashSet<int> enabledExchangeSet,
        HashSet<int> dataOnlyExchangeIds,
        HashSet<int> circuitBrokenExchangeIds,
        HashSet<int> enabledAssetSet,
        UserConfiguration userConfig,
        SkipReasonTracker tracker);

    /// <summary>
    /// Filters above-threshold opportunities by active positions and cooldowns.
    /// </summary>
    List<ArbitrageOpportunityDto> FilterCandidates(
        List<ArbitrageOpportunityDto> userOpportunities,
        HashSet<string> allActiveKeys,
        string userId,
        SkipReasonTracker tracker,
        out List<(string Asset, TimeSpan Remaining)> cooldownSkips);

    /// <summary>
    /// Finds the best net-positive opportunity below the user's threshold (adaptive fallback).
    /// </summary>
    List<ArbitrageOpportunityDto> FindAdaptiveCandidates(
        List<ArbitrageOpportunityDto> allNetPositive,
        HashSet<int> enabledExchangeSet,
        HashSet<int> dataOnlyExchangeIds,
        HashSet<int> circuitBrokenExchangeIds,
        HashSet<int> enabledAssetSet,
        HashSet<string> activeKeys,
        string userId,
        SkipReasonTracker tracker);
}
