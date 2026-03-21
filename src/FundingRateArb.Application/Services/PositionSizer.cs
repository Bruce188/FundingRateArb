using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class PositionSizer : IPositionSizer
{
    private readonly IUnitOfWork _uow;
    private readonly IYieldCalculator _yieldCalculator;
    private readonly IBalanceAggregator _balanceAggregator;

    public PositionSizer(IUnitOfWork uow, IYieldCalculator yieldCalculator, IBalanceAggregator balanceAggregator)
    {
        _uow = uow;
        _yieldCalculator = yieldCalculator;
        _balanceAggregator = balanceAggregator;
    }

    public async Task<decimal[]> CalculateBatchSizesAsync(
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        AllocationStrategy strategy,
        string userId,
        CancellationToken ct = default)
    {
        if (opportunities.Count == 0)
            return [];

        var config = await _uow.BotConfig.GetActiveAsync();
        var openPositions = await _uow.Positions.GetOpenAsync();
        var allocatedCapital = openPositions.Sum(p => p.SizeUsdc);

        // Use real exchange balance, capped by configured TotalCapitalUsdc
        var balanceSnapshot = await _balanceAggregator.GetBalanceSnapshotAsync(userId, ct);
        var realCapital = Math.Min(balanceSnapshot.TotalAvailableUsdc, config.TotalCapitalUsdc);
        var availableCapital = Math.Max(0, realCapital - allocatedCapital);
        var totalCapital = availableCapital * config.MaxCapitalPerPosition;
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

        // C2: Cap each position by its liquidity limit (compare notional, not margin)
        for (int i = 0; i < sizes.Length; i++)
        {
            var minVol = Math.Min(opportunities[i].LongVolume24h, opportunities[i].ShortVolume24h);
            var liquidityLimit = minVol * config.VolumeFraction;
            var notional = sizes[i] * config.DefaultLeverage;
            if (notional > liquidityLimit)
                sizes[i] = liquidityLimit / config.DefaultLeverage;
        }

        // C1: Breakeven gate — reject positions that can't break even in time
        for (int i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] <= 0) continue;
            var opp = opportunities[i];
            var entryFeeRate = opp.SpreadPerHour - opp.NetYieldPerHour;
            if (entryFeeRate < 0) { sizes[i] = 0; continue; }
            var breakEvenHours = _yieldCalculator.BreakEvenHours(entryFeeRate, opp.NetYieldPerHour);
            if (breakEvenHours > config.BreakevenHoursMax)
                sizes[i] = 0;
        }

        // H2: Enforce minimum position size (exchange minimums)
        for (int i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] > 0 && sizes[i] < config.MinPositionSizeUsdc)
                sizes[i] = 0;
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
