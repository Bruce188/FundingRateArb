using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class PositionSizer : IPositionSizer
{
    private readonly IUnitOfWork _uow;
    private readonly IYieldCalculator _yieldCalculator;
    private readonly IBalanceAggregator _balanceAggregator;
    private readonly IUserSettingsService _userSettings;

    public PositionSizer(IUnitOfWork uow, IYieldCalculator yieldCalculator, IBalanceAggregator balanceAggregator, IUserSettingsService userSettings)
    {
        _uow = uow;
        _yieldCalculator = yieldCalculator;
        _balanceAggregator = balanceAggregator;
        _userSettings = userSettings;
    }

    public async Task<decimal[]> CalculateBatchSizesAsync(
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        AllocationStrategy strategy,
        string userId,
        UserConfiguration? userConfig = null,
        CancellationToken ct = default)
    {
        if (opportunities.Count == 0)
        {
            return [];
        }

        var config = await _uow.BotConfig.GetActiveAsync();
        userConfig ??= await _userSettings.GetOrCreateConfigAsync(userId);
        var effectiveLeverage = userConfig.DefaultLeverage > 0 ? userConfig.DefaultLeverage : config.DefaultLeverage;
        var userActivePositions = await _uow.Positions.GetByUserAndStatusesAsync(userId, PositionStatus.Open, PositionStatus.Opening);
        var allocatedCapital = userActivePositions.Sum(p => p.SizeUsdc);

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
                {
                    sizes[i] = equalSlice;
                }

                break;

            case AllocationStrategy.WeightedSpread:
                {
                    var totalYield = opportunities.Sum(o => o.NetYieldPerHour);
                    if (totalYield <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < sizes.Length; i++)
                    {
                        sizes[i] = totalCapital * (opportunities[i].NetYieldPerHour / totalYield);
                    }

                    break;
                }

            case AllocationStrategy.RiskAdjusted:
                {
                    // Weight by yield * sqrt(minVolume) — penalizes low-volume opportunities
                    var scores = opportunities
                        .Select(o => o.NetYieldPerHour * (decimal)Math.Sqrt((double)Math.Min(o.LongVolume24h, o.ShortVolume24h)))
                        .ToArray();
                    var totalScore = scores.Sum();
                    if (totalScore <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < sizes.Length; i++)
                    {
                        sizes[i] = totalCapital * (scores[i] / totalScore);
                    }

                    break;
                }
        }

        // Exposure limit enforcement: cap by per-asset and per-exchange limits
        // Track batch-allocated exposure so multiple opportunities for the same asset/exchange
        // don't exceed limits in aggregate
        var batchAssetExposure = new Dictionary<int, decimal>();
        var batchExchangeExposure = new Dictionary<int, decimal>();

        // Pre-aggregate existing exposure by asset and exchange for O(1) lookups
        var existingAssetExposure = userActivePositions
            .GroupBy(p => p.AssetId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.SizeUsdc));

        var existingExchangeExposure = new Dictionary<int, decimal>();
        foreach (var p in userActivePositions)
        {
            existingExchangeExposure[p.LongExchangeId] = existingExchangeExposure.GetValueOrDefault(p.LongExchangeId) + p.SizeUsdc;
            existingExchangeExposure[p.ShortExchangeId] = existingExchangeExposure.GetValueOrDefault(p.ShortExchangeId) + p.SizeUsdc;
        }

        for (int i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] <= 0)
            {
                continue;
            }

            var opp = opportunities[i];

            // Per-asset exposure
            var currentAssetExposure = existingAssetExposure.GetValueOrDefault(opp.AssetId);
            var batchAsset = batchAssetExposure.GetValueOrDefault(opp.AssetId, 0m);
            var maxNewAsset = (config.MaxExposurePerAsset * realCapital) - currentAssetExposure - batchAsset;
            if (maxNewAsset <= 0) { sizes[i] = 0; continue; }
            sizes[i] = Math.Min(sizes[i], maxNewAsset);

            // Per-exchange exposure: check both long and short exchanges + batch allocations
            var currentLongExposure = existingExchangeExposure.GetValueOrDefault(opp.LongExchangeId);
            var batchLong = batchExchangeExposure.GetValueOrDefault(opp.LongExchangeId, 0m);
            var maxNewLong = (config.MaxExposurePerExchange * realCapital) - currentLongExposure - batchLong;
            if (maxNewLong <= 0) { sizes[i] = 0; continue; }
            sizes[i] = Math.Min(sizes[i], maxNewLong);

            var currentShortExposure = existingExchangeExposure.GetValueOrDefault(opp.ShortExchangeId);
            var batchShort = batchExchangeExposure.GetValueOrDefault(opp.ShortExchangeId, 0m);
            var maxNewShort = (config.MaxExposurePerExchange * realCapital) - currentShortExposure - batchShort;
            if (maxNewShort <= 0) { sizes[i] = 0; continue; }
            sizes[i] = Math.Min(sizes[i], maxNewShort);

            // Update batch tracking with this opportunity's final allocation
            batchAssetExposure[opp.AssetId] = batchAsset + sizes[i];
            batchExchangeExposure[opp.LongExchangeId] = batchLong + sizes[i];
            batchExchangeExposure[opp.ShortExchangeId] = batchShort + sizes[i];
        }

        // Per-exchange balance cap: each leg needs the full margin on its own exchange
        var exchangeBalances = balanceSnapshot.Balances
            .Where(b => b.ErrorMessage is null)
            .GroupBy(b => b.ExchangeId)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.AvailableUsdc));

        for (int i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] <= 0)
            {
                continue;
            }

            var opp = opportunities[i];
            var longBalance = exchangeBalances.GetValueOrDefault(opp.LongExchangeId);
            var shortBalance = exchangeBalances.GetValueOrDefault(opp.ShortExchangeId);
            var maxByBalance = Math.Min(longBalance, shortBalance);
            if (maxByBalance <= 0) { sizes[i] = 0; continue; }
            sizes[i] = Math.Min(sizes[i], maxByBalance);
        }

        // C2: Cap each position by its liquidity limit (compare notional, not margin)
        for (int i = 0; i < sizes.Length; i++)
        {
            var minVol = Math.Min(opportunities[i].LongVolume24h, opportunities[i].ShortVolume24h);
            var liquidityLimit = minVol * config.VolumeFraction;
            var notional = sizes[i] * effectiveLeverage;
            if (notional > liquidityLimit)
            {
                sizes[i] = liquidityLimit / effectiveLeverage;
            }
        }

        // C1: Breakeven gate — reject positions that can't break even in time
        for (int i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] <= 0)
            {
                continue;
            }

            var opp = opportunities[i];
            var entryFeeRate = opp.SpreadPerHour - opp.NetYieldPerHour;
            if (entryFeeRate < 0) { sizes[i] = 0; continue; }
            var breakEvenHours = _yieldCalculator.BreakEvenHours(entryFeeRate, opp.NetYieldPerHour);
            if (breakEvenHours > config.BreakevenHoursMax)
            {
                sizes[i] = 0;
            }
        }

        // H2: Enforce minimum position size (exchange minimums)
        for (int i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] > 0 && sizes[i] < config.MinPositionSizeUsdc)
            {
                sizes[i] = 0;
            }
        }

        return sizes;
    }

    public static decimal RoundToStepSize(decimal quantity, decimal stepSize, int decimals)
    {
        if (stepSize <= 0)
        {
            return Math.Round(quantity, decimals);
        }

        var steps = Math.Floor(quantity / stepSize);
        var rounded = steps * stepSize;
        return Math.Round(rounded, decimals);
    }
}
