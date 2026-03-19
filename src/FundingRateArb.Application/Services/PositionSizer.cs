using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class PositionSizer : IPositionSizer
{
    private readonly IUnitOfWork _uow;
    private readonly IYieldCalculator _yieldCalculator;

    public PositionSizer(IUnitOfWork uow, IYieldCalculator yieldCalculator)
    {
        _uow = uow;
        _yieldCalculator = yieldCalculator;
    }

    public async Task<decimal> CalculateOptimalSizeAsync(ArbitrageOpportunityDto opp)
    {
        if (opp.NetYieldPerHour <= 0)
            return 0m;

        var config = await _uow.BotConfig.GetActiveAsync();

        // Break-even limit: fee rate = gross spread minus net yield (fees subtracted by SignalEngine)
        var entryFeeRate = opp.SpreadPerHour - opp.NetYieldPerHour;
        // H4: Negative entryFeeRate means spread < net yield — invalid opportunity, reject
        if (entryFeeRate < 0)
            return 0m;
        var breakEvenHours = _yieldCalculator.BreakEvenHours(entryFeeRate, opp.NetYieldPerHour);
        if (breakEvenHours > config.BreakevenHoursMax)
            return 0m;

        // Capital limit is the max collateral (margin) per leg.
        // Connectors multiply by leverage to get notional.
        var capitalLimit = config.TotalCapitalUsdc
                           * config.MaxCapitalPerPosition;

        var minVolume = Math.Min(opp.LongVolume24h, opp.ShortVolume24h);
        var liquidityLimit = minVolume * config.VolumeFraction;

        return Math.Min(capitalLimit, liquidityLimit);
    }

    public async Task<decimal[]> CalculateBatchSizesAsync(
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        AllocationStrategy strategy)
    {
        if (opportunities.Count == 0)
            return [];

        var config = await _uow.BotConfig.GetActiveAsync();
        var totalCapital = config.TotalCapitalUsdc * config.MaxCapitalPerPosition;
        var sizes = new decimal[opportunities.Count];

        switch (strategy)
        {
            case AllocationStrategy.Concentrated:
                sizes[0] = totalCapital;
                break;

            case AllocationStrategy.EqualSpread:
                var equalSlice = totalCapital / opportunities.Count;
                for (int i = 0; i < sizes.Length; i++)
                    sizes[i] = equalSlice;
                break;

            case AllocationStrategy.WeightedSpread:
            {
                var totalYield = opportunities.Sum(o => o.NetYieldPerHour);
                if (totalYield <= 0) break;
                for (int i = 0; i < sizes.Length; i++)
                    sizes[i] = totalCapital * (opportunities[i].NetYieldPerHour / totalYield);
                break;
            }

            case AllocationStrategy.RiskAdjusted:
            {
                // Weight by yield * sqrt(minVolume) — penalizes low-volume opportunities
                var scores = opportunities
                    .Select(o => o.NetYieldPerHour * (decimal)Math.Sqrt((double)Math.Min(o.LongVolume24h, o.ShortVolume24h)))
                    .ToArray();
                var totalScore = scores.Sum();
                if (totalScore <= 0) break;
                for (int i = 0; i < sizes.Length; i++)
                    sizes[i] = totalCapital * (scores[i] / totalScore);
                break;
            }
        }

        // Cap each position by its liquidity limit
        for (int i = 0; i < sizes.Length; i++)
        {
            var minVol = Math.Min(opportunities[i].LongVolume24h, opportunities[i].ShortVolume24h);
            var liquidityLimit = minVol * config.VolumeFraction;
            sizes[i] = Math.Min(sizes[i], liquidityLimit);
        }

        return sizes;
    }

    public static decimal RoundToStepSize(decimal quantity, decimal stepSize, int decimals)
    {
        if (stepSize <= 0) return Math.Round(quantity, decimals);
        var steps = Math.Floor(quantity / stepSize);
        var rounded = steps * stepSize;
        return Math.Round(rounded, decimals);
    }
}
