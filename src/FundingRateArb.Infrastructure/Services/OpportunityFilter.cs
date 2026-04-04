using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class OpportunityFilter : IOpportunityFilter
{
    private readonly ICircuitBreakerManager _circuitBreaker;
    private readonly ILogger<OpportunityFilter> _logger;

    public OpportunityFilter(ICircuitBreakerManager circuitBreaker, ILogger<OpportunityFilter> logger)
    {
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    public List<ArbitrageOpportunityDto> FilterUserOpportunities(
        List<ArbitrageOpportunityDto> allOpportunities,
        HashSet<int> enabledExchangeSet,
        HashSet<int> dataOnlyExchangeIds,
        HashSet<int> circuitBrokenExchangeIds,
        HashSet<int> enabledAssetSet,
        UserConfiguration userConfig,
        SkipReasonTracker tracker)
    {
        var userOpportunities = new List<ArbitrageOpportunityDto>();
        foreach (var o in allOpportunities)
        {
            var key = o.OpportunityKey();

            if (!enabledExchangeSet.Contains(o.LongExchangeId) || !enabledExchangeSet.Contains(o.ShortExchangeId))
            {
                tracker.ExchangeDisabledKeys.Add(key);
                continue;
            }
            if (dataOnlyExchangeIds.Contains(o.LongExchangeId) || dataOnlyExchangeIds.Contains(o.ShortExchangeId))
            {
                tracker.ExchangeDisabledKeys.Add(key);
                continue;
            }
            if (circuitBrokenExchangeIds.Contains(o.LongExchangeId) || circuitBrokenExchangeIds.Contains(o.ShortExchangeId))
            {
                tracker.CircuitBrokenKeys.Add(key);
                continue;
            }
            if (!enabledAssetSet.Contains(o.AssetId))
            {
                tracker.AssetDisabledKeys.Add(key);
                continue;
            }
            if (o.NetYieldPerHour < userConfig.OpenThreshold)
            {
                tracker.BelowThresholdCount++;
                if (o.NetYieldPerHour > tracker.BestBelowThresholdYield)
                {
                    tracker.BestBelowThresholdYield = o.NetYieldPerHour;
                }
                continue;
            }
            userOpportunities.Add(o);
        }
        return userOpportunities;
    }

    public List<ArbitrageOpportunityDto> FilterCandidates(
        List<ArbitrageOpportunityDto> userOpportunities,
        HashSet<string> allActiveKeys,
        string userId,
        SkipReasonTracker tracker,
        out List<(string Asset, TimeSpan Remaining)> cooldownSkips)
    {
        cooldownSkips = new List<(string Asset, TimeSpan Remaining)>();
        var localCooldownSkips = cooldownSkips;

        var filteredCandidates = userOpportunities
            .Where(opp =>
            {
                var key = opp.OpportunityKey();
                if (allActiveKeys.Contains(key))
                {
                    return false;
                }
                // Per-user cooldown key
                var cooldownKey = $"{userId}:{key}";
                if (_circuitBreaker.IsOnCooldown(cooldownKey, out var remaining))
                {
                    _logger.LogDebug(
                        "Skipping {Asset} {Long}/{Short} for user {UserId} — on cooldown for {Remaining}",
                        opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, userId, remaining);
                    localCooldownSkips.Add((opp.AssetSymbol, remaining));
                    tracker.CooldownKeys.Add(key);
                    return false;
                }
                // Asset-exchange cooldown — skip if same asset+exchange combo has repeated failures
                if (_circuitBreaker.IsAssetExchangeOnCooldown(opp.AssetId, opp.LongExchangeId))
                {
                    return false;
                }
                if (_circuitBreaker.IsAssetExchangeOnCooldown(opp.AssetId, opp.ShortExchangeId))
                {
                    return false;
                }
                return true;
            })
            .ToList();

        return filteredCandidates;
    }

    public List<ArbitrageOpportunityDto> FindAdaptiveCandidates(
        List<ArbitrageOpportunityDto> allNetPositive,
        HashSet<int> enabledExchangeSet,
        HashSet<int> dataOnlyExchangeIds,
        HashSet<int> circuitBrokenExchangeIds,
        HashSet<int> enabledAssetSet,
        HashSet<string> activeKeys,
        string userId,
        SkipReasonTracker tracker)
    {
        return allNetPositive
            .Where(o => enabledExchangeSet.Contains(o.LongExchangeId) && enabledExchangeSet.Contains(o.ShortExchangeId))
            .Where(o => !dataOnlyExchangeIds.Contains(o.LongExchangeId) && !dataOnlyExchangeIds.Contains(o.ShortExchangeId))
            .Where(o => !circuitBrokenExchangeIds.Contains(o.LongExchangeId) && !circuitBrokenExchangeIds.Contains(o.ShortExchangeId))
            .Where(o => enabledAssetSet.Contains(o.AssetId))
            .Where(opp =>
            {
                var key = opp.OpportunityKey();
                if (activeKeys.Contains(key))
                {
                    return false;
                }

                var cooldownKey = $"{userId}:{key}";
                if (_circuitBreaker.IsOnCooldown(cooldownKey, out _))
                {
                    tracker.CooldownKeys.Add(key);
                    return false;
                }

                // Asset-exchange cooldown
                if (_circuitBreaker.IsAssetExchangeOnCooldown(opp.AssetId, opp.LongExchangeId))
                {
                    return false;
                }
                if (_circuitBreaker.IsAssetExchangeOnCooldown(opp.AssetId, opp.ShortExchangeId))
                {
                    return false;
                }

                return true;
            })
            .OrderByDescending(o => o.NetYieldPerHour)
            .Take(1)
            .ToList();
    }
}
