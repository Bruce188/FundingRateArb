using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public class PortfolioRebalancer : IPortfolioRebalancer
{
    private const decimal MinHoursBeforeRebalance = 1.0m;

    public Task<List<RebalanceRecommendationDto>> EvaluateAsync(
        IReadOnlyList<ArbitragePosition> openPositions,
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        BotConfiguration config,
        CancellationToken ct = default)
    {
        var recommendations = new List<RebalanceRecommendationDto>();

        if (!config.RebalanceEnabled || openPositions.Count == 0 || opportunities.Count == 0)
            return Task.FromResult(recommendations);

        // Build a set of currently held asset/exchange combos to skip
        var heldKeys = openPositions
            .Select(p => $"{p.AssetId}-{p.LongExchangeId}-{p.ShortExchangeId}")
            .ToHashSet();

        // For each open position held > 1 hour
        var candidates = new List<(RebalanceRecommendationDto Recommendation, string OppKey, decimal Improvement)>();

        foreach (var pos in openPositions)
        {
            var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;
            if (hoursOpen < MinHoursBeforeRebalance)
                continue;

            var remainingHours = Math.Max(0, config.MaxHoldTimeHours - hoursOpen);
            if (remainingHours <= 0)
                continue; // will be closed by MaxHoldTime soon anyway

            var remainingExpectedPnl = pos.CurrentSpreadPerHour * remainingHours * pos.SizeUsdc;

            // Evaluate each opportunity not already held
            foreach (var opp in opportunities)
            {
                var oppKey = $"{opp.AssetId}-{opp.LongExchangeId}-{opp.ShortExchangeId}";
                if (heldKeys.Contains(oppKey))
                    continue;

                // Switching cost: close current (2 trades) + open new (2 trades) + eventual close new (2 trades)
                // Assumes new position uses same SizeUsdc and Leverage as current position
                var closeFee = PositionHealthMonitor.GetTakerFeeRate(pos.LongExchange?.Name, pos.ShortExchange?.Name);
                var openFee = PositionHealthMonitor.GetTakerFeeRate(opp.LongExchangeName, opp.ShortExchangeName);
                // F7: Include eventual close-new fee so improvement isn't overstated
                var closeFeeNew = PositionHealthMonitor.GetTakerFeeRate(opp.LongExchangeName, opp.ShortExchangeName);
                var switchingCost = pos.SizeUsdc * pos.Leverage * (closeFee + openFee + closeFeeNew);

                var newExpectedPnl = opp.NetYieldPerHour * remainingHours * pos.SizeUsdc - switchingCost;
                var improvement = newExpectedPnl - remainingExpectedPnl;

                if (improvement > config.RebalanceMinImprovement * pos.SizeUsdc)
                {
                    candidates.Add((new RebalanceRecommendationDto(
                        pos.Id,
                        pos.Asset?.Symbol ?? $"#{pos.AssetId}",
                        pos.CurrentSpreadPerHour,
                        remainingExpectedPnl,
                        opp.AssetSymbol,
                        opp.LongExchangeName,
                        opp.ShortExchangeName,
                        opp.SpreadPerHour,
                        improvement), oppKey, improvement));
                }
            }
        }

        // Sort by improvement descending
        candidates.Sort((a, b) => b.Improvement.CompareTo(a.Improvement));

        // Deduplicate: each opportunity can only replace one position (best match wins)
        // Use consistent ID-based keys for both held positions and opportunities
        var usedOpps = new HashSet<string>();
        var usedPositions = new HashSet<int>();

        foreach (var (rec, oppKey, _) in candidates)
        {
            if (usedOpps.Contains(oppKey) || usedPositions.Contains(rec.PositionId))
                continue;

            usedOpps.Add(oppKey);
            usedPositions.Add(rec.PositionId);
            recommendations.Add(rec);
        }

        return Task.FromResult(recommendations);
    }
}
