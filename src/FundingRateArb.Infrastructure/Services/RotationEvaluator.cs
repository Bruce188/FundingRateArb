using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class RotationEvaluator : IRotationEvaluator
{
    private readonly ILogger<RotationEvaluator> _logger;

    public RotationEvaluator(ILogger<RotationEvaluator> logger)
    {
        _logger = logger;
    }

    public RotationRecommendationDto? Evaluate(
        IReadOnlyList<ArbitragePosition> openPositions,
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        UserConfiguration userConfig,
        BotConfiguration globalConfig)
    {
        if (openPositions.Count == 0 || opportunities.Count == 0)
            return null;

        // Only consider Open positions (not Opening, Closing, etc.)
        var eligiblePositions = openPositions
            .Where(p => p.Status == PositionStatus.Open)
            .ToList();

        if (eligiblePositions.Count == 0)
            return null;

        // Find worst position by CurrentSpreadPerHour
        var worstPosition = eligiblePositions.MinBy(p => p.CurrentSpreadPerHour)!;

        _logger.LogDebug(
            "Evaluating rotation: worst position {PositionId} ({Asset}) spread={Spread:F6}/hr",
            worstPosition.Id, worstPosition.Asset?.Symbol ?? "?", worstPosition.CurrentSpreadPerHour);

        // Build set of occupied opportunity keys to exclude from candidates
        var occupiedKeys = eligiblePositions
            .Select(p => (p.AssetId, p.LongExchangeId, p.ShortExchangeId))
            .ToHashSet();

        // Find best available opportunity (not occupied by any current position)
        var bestOpportunity = opportunities
            .Where(o => !occupiedKeys.Contains((o.AssetId, o.LongExchangeId, o.ShortExchangeId)))
            .MaxBy(o => o.NetYieldPerHour);

        if (bestOpportunity is null)
        {
            _logger.LogDebug("No available replacement opportunities after excluding occupied positions");
            return null;
        }

        // Calculate improvement
        var improvement = bestOpportunity.NetYieldPerHour - worstPosition.CurrentSpreadPerHour;

        // Check threshold
        if (improvement <= userConfig.RotationThresholdPerHour)
        {
            _logger.LogDebug(
                "Rotation improvement {Improvement:F6}/hr below threshold {Threshold:F6}/hr",
                improvement, userConfig.RotationThresholdPerHour);
            return null;
        }

        // Check hold time (with CloseThreshold exception)
        var minutesOpen = (DateTime.UtcNow - worstPosition.OpenedAt).TotalMinutes;

        if (worstPosition.CurrentSpreadPerHour < globalConfig.CloseThreshold)
        {
            _logger.LogDebug(
                "Position {PositionId} spread {Spread:F6}/hr below close threshold {Threshold:F6}/hr — skipping hold time check",
                worstPosition.Id, worstPosition.CurrentSpreadPerHour, globalConfig.CloseThreshold);
        }
        else if (minutesOpen < userConfig.MinHoldBeforeRotationMinutes)
        {
            _logger.LogDebug(
                "Position {PositionId} held {Minutes:F0} min < minimum {MinMinutes} min — rotation skipped",
                worstPosition.Id, minutesOpen, userConfig.MinHoldBeforeRotationMinutes);
            return null;
        }

        var recommendation = new RotationRecommendationDto(
            PositionId: worstPosition.Id,
            PositionAsset: worstPosition.Asset?.Symbol ?? $"Asset#{worstPosition.AssetId}",
            CurrentSpreadPerHour: worstPosition.CurrentSpreadPerHour,
            ReplacementAssetId: bestOpportunity.AssetId,
            ReplacementAsset: bestOpportunity.AssetSymbol,
            ReplacementLongExchange: bestOpportunity.LongExchangeName,
            ReplacementShortExchange: bestOpportunity.ShortExchangeName,
            ReplacementLongExchangeId: bestOpportunity.LongExchangeId,
            ReplacementShortExchangeId: bestOpportunity.ShortExchangeId,
            ReplacementNetYieldPerHour: bestOpportunity.NetYieldPerHour,
            ImprovementPerHour: improvement);

        _logger.LogInformation(
            "Rotation recommended: position {PositionId} ({Asset} spread={Spread:F6}/hr) → {Replacement} (yield={Yield:F6}/hr, improvement={Improvement:F6}/hr)",
            recommendation.PositionId, recommendation.PositionAsset, recommendation.CurrentSpreadPerHour,
            recommendation.ReplacementAsset, recommendation.ReplacementNetYieldPerHour, recommendation.ImprovementPerHour);

        return recommendation;
    }
}
