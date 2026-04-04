using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class SignalEngine : ISignalEngine
{
    private readonly IUnitOfWork _uow;
    private readonly IMarketDataCache _cache;
    private readonly IRatePredictionService? _predictionService;
    private readonly ILeverageTierProvider? _tierProvider;
    private readonly ILogger<SignalEngine>? _logger;

    public SignalEngine(IUnitOfWork uow, IMarketDataCache cache, IRatePredictionService? predictionService = null, ILeverageTierProvider? tierProvider = null, ILogger<SignalEngine>? logger = null)
    {
        _uow = uow;
        _cache = cache;
        _predictionService = predictionService;
        _tierProvider = tierProvider;
        _logger = logger;
    }

    public async Task<List<ArbitrageOpportunityDto>> GetOpportunitiesAsync(CancellationToken ct = default)
    {
        var result = await GetOpportunitiesWithDiagnosticsAsync(ct);
        return result.Opportunities;
    }

    public async Task<OpportunityResultDto> GetOpportunitiesWithDiagnosticsAsync(CancellationToken ct = default)
    {
        var config = await _uow.BotConfig.GetActiveAsync();
        var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        var rates = latestRates.Where(r => r.Asset is not null && r.Exchange is not null).ToList();

        var diagnostics = new PipelineDiagnosticsDto
        {
            TotalRatesLoaded = rates.Count,
            StalenessMinutes = Math.Max(config.RateStalenessMinutes, 1),
            MinVolumeThreshold = config.MinVolume24hUsdc,
            OpenThreshold = config.OpenThreshold
        };

        // H3: Filter out stale rates (guard against zero — would filter everything)
        var cutoff = DateTime.UtcNow.AddMinutes(-diagnostics.StalenessMinutes);
        rates = rates.Where(r => r.RecordedAt >= cutoff).ToList();
        diagnostics.RatesAfterStalenessFilter = rates.Count;

        // Load predictions for enrichment (non-critical — null if service unavailable)
        Dictionary<(int AssetId, int ExchangeId), RatePredictionDto>? predictionLookup = null;
        if (_predictionService is not null)
        {
            try
            {
                var predictions = await _predictionService.GetPredictionsAsync(ct);
                predictionLookup = predictions
                    .GroupBy(p => (p.AssetId, p.ExchangeId))
                    .ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Predictions are informational — don't block opportunity generation
                _logger?.LogWarning(ex, "Failed to load rate predictions; continuing without prediction data");
            }
        }

        // Batch-load recent funding rate history for trend analysis (parallelized)
        var distinctKeys = rates.Select(r => (r.AssetId, r.ExchangeId)).Distinct().ToList();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow;
        var historyTasks = distinctKeys.Select(async key =>
        {
            var history = await _uow.FundingRates.GetHistoryAsync(
                key.AssetId, key.ExchangeId, from, to,
                take: config.MinConsecutiveFavorableCycles);
            return (key, history);
        });
        var historyResults = await Task.WhenAll(historyTasks);
        var historyLookup = historyResults.ToDictionary(r => r.key, r => r.history);

        var opportunities = new List<ArbitrageOpportunityDto>();
        var netPositiveList = new List<ArbitrageOpportunityDto>();

        foreach (var group in rates.Where(r => r.Asset?.Symbol is not null).GroupBy(r => r.Asset!.Symbol))
        {
            var symbol = group.Key;
            var assetRates = group.ToList();

            for (int i = 0; i < assetRates.Count; i++)
            {
                for (int j = i + 1; j < assetRates.Count; j++)
                {
                    var a = assetRates[i];
                    var b = assetRates[j];

                    var (longR, shortR) = a.RatePerHour <= b.RatePerHour ? (a, b) : (b, a);

                    diagnostics.TotalPairsEvaluated++;

                    var diff = shortR.RatePerHour - longR.RatePerHour;

                    // Track best raw spread across ALL pairs, before any filter
                    if (diff > diagnostics.BestRawSpread)
                    {
                        diagnostics.BestRawSpread = diff;
                    }

                    // M2: Skip opportunities where either leg has insufficient volume
                    if (longR.Volume24hUsd < config.MinVolume24hUsdc || shortR.Volume24hUsd < config.MinVolume24hUsdc)
                    {
                        diagnostics.PairsFilteredByVolume++;
                        continue;
                    }

                    // Use DB-stored TakerFeeRate when available; fall back to shared constants.
                    var longFee = longR.Exchange.TakerFeeRate * 2
                                   ?? ExchangeFeeConstants.GetTakerFeeRate(longR.Exchange.Name) * 2;
                    var shortFee = shortR.Exchange.TakerFeeRate * 2
                                   ?? ExchangeFeeConstants.GetTakerFeeRate(shortR.Exchange.Name) * 2;
                    var amortHours = Math.Max(config.FeeAmortizationHours, 1);
                    var feePerHour = (longFee + shortFee) / amortHours;
                    var net = diff - feePerHour;

                    // Subtract slippage buffer (one-time cost amortized over hold period like fees)
                    var slippagePerHour = config.SlippageBufferBps / 10_000m / amortHours;
                    net -= slippagePerHour;

                    // Apply funding rebate BEFORE break-even computation so break-even uses post-rebate net yield.
                    // Long leg pays when rate > 0; guard prevents incorrect boost when rate is negative.
                    if (longR.Exchange.FundingRebateRate > 0 && longR.RatePerHour > 0)
                    {
                        var effectiveLongRebate = Math.Clamp(longR.Exchange.FundingRebateRate, 0m, 1m);
                        var rebateBoost = longR.RatePerHour * effectiveLongRebate;
                        net += rebateBoost;
                    }
                    // Short side pays when rate is negative. Rebate reduces that cost,
                    // which improves net yield (hence +=).
                    if (shortR.Exchange.FundingRebateRate > 0 && shortR.RatePerHour < 0)
                    {
                        var effectiveShortRebate = Math.Clamp(shortR.Exchange.FundingRebateRate, 0m, 1m);
                        var rebateBoost = Math.Abs(shortR.RatePerHour) * effectiveShortRebate;
                        net += rebateBoost;
                    }

                    // Break-even analysis: hours to recover entry costs from post-rebate net yield
                    var totalEntryCost = (longFee + shortFee) + (config.SlippageBufferBps / 10_000m);
                    var breakEvenHours = net > 0 ? totalEntryCost / net : (decimal?)null;

                    // Compute minutes to next settlement from either leg (use minimum)
                    int? minutesToSettlement = null;
                    var now = DateTime.UtcNow;
                    var longNext = _cache.GetNextSettlement(longR.Exchange.Name, symbol);
                    var shortNext = _cache.GetNextSettlement(shortR.Exchange.Name, symbol);
                    if (longNext.HasValue || shortNext.HasValue)
                    {
                        var longMin = longNext.HasValue ? (int)Math.Max(0, (longNext.Value - now).TotalMinutes) : int.MaxValue;
                        var shortMin = shortNext.HasValue ? (int)Math.Max(0, (shortNext.Value - now).TotalMinutes) : int.MaxValue;
                        minutesToSettlement = Math.Min(longMin, shortMin);
                        if (minutesToSettlement == int.MaxValue)
                        {
                            minutesToSettlement = null;
                        }
                    }

                    // Adjust for exchange-specific timing deviations (e.g. Aster settles 15s after boundary)
                    if (minutesToSettlement.HasValue)
                    {
                        var longDeviationSec = Math.Clamp(longR.Exchange.FundingTimingDeviationSeconds, 0, 300);
                        var shortDeviationSec = Math.Clamp(shortR.Exchange.FundingTimingDeviationSeconds, 0, 300);
                        var maxDeviationSec = Math.Max(longDeviationSec, shortDeviationSec);
                        var maxDeviationMin = (maxDeviationSec + 59) / 60;
                        if (maxDeviationMin > 0)
                        {
                            minutesToSettlement = Math.Max(0, minutesToSettlement.Value - maxDeviationMin);
                        }
                    }

                    // Apply funding window boost: 20% yield boost when settlement is imminent
                    var boostedNet = net;
                    if (minutesToSettlement.HasValue && minutesToSettlement.Value <= config.FundingWindowMinutes)
                    {
                        boostedNet = net * 1.2m;
                    }

                    // Look up predictions for both legs
                    RatePredictionDto? longPred = null, shortPred = null;
                    predictionLookup?.TryGetValue((longR.AssetId, longR.ExchangeId), out longPred);
                    predictionLookup?.TryGetValue((shortR.AssetId, shortR.ExchangeId), out shortPred);

                    var dto = new ArbitrageOpportunityDto
                    {
                        AssetSymbol = symbol,
                        AssetId = longR.AssetId,
                        LongExchangeName = longR.Exchange.Name,
                        LongExchangeId = longR.ExchangeId,
                        ShortExchangeName = shortR.Exchange.Name,
                        ShortExchangeId = shortR.ExchangeId,
                        LongRatePerHour = longR.RatePerHour,
                        ShortRatePerHour = shortR.RatePerHour,
                        SpreadPerHour = diff,
                        NetYieldPerHour = net,
                        BoostedNetYieldPerHour = boostedNet,
                        AnnualizedYield = net * 24m * 365m,
                        LongVolume24h = longR.Volume24hUsd,
                        ShortVolume24h = shortR.Volume24hUsd,
                        LongMarkPrice = longR.MarkPrice,
                        ShortMarkPrice = shortR.MarkPrice,
                        MinutesToNextSettlement = minutesToSettlement,
                        PredictedLongRate = longPred?.PredictedRatePerHour,
                        PredictedShortRate = shortPred?.PredictedRatePerHour,
                        PredictedSpread = longPred is not null && shortPred is not null
                            ? shortPred.PredictedRatePerHour - longPred.PredictedRatePerHour
                            : null,
                        PredictionConfidence = longPred is not null && shortPred is not null
                            ? Math.Min(longPred.Confidence, shortPred.Confidence)
                            : null,
                        PredictedTrend = shortPred?.TrendDirection,
                        BreakEvenHours = breakEvenHours,
                    };

                    // Compute leverage-adjusted metrics when tier data is available
                    if (_tierProvider is not null)
                    {
                        var cappedLeverage = Math.Max(1, Math.Min(config.DefaultLeverage, config.MaxLeverageCap));
                        var refNotional = config.TotalCapitalUsdc * config.MaxCapitalPerPosition * cappedLeverage;
                        var longMaxLev = _tierProvider.GetEffectiveMaxLeverage(longR.Exchange.Name, symbol!, refNotional);
                        var shortMaxLev = _tierProvider.GetEffectiveMaxLeverage(shortR.Exchange.Name, symbol!, refNotional);
                        var tierMax = Math.Min(longMaxLev, shortMaxLev);
                        var effectiveLev = Math.Min(config.DefaultLeverage, Math.Min(config.MaxLeverageCap, tierMax));
                        if (effectiveLev > 0 && effectiveLev < int.MaxValue)
                        {
                            dto.EffectiveLeverage = effectiveLev;
                            dto.ReturnOnCapitalPerHour = net * effectiveLev;
                            dto.AprOnCapital = dto.ReturnOnCapitalPerHour.Value * 24m * 365m * 100m;
                            // BreakEvenCycles: total entry cost / (net yield per cycle * leverage)
                            var entrySpreadCost = (longFee + shortFee);
                            if (net > 0)
                            {
                                dto.BreakEvenCycles = entrySpreadCost / (net * effectiveLev);
                            }
                        }
                    }

                    // Break-even filter: skip opportunities that take too long to recover entry costs
                    if (breakEvenHours.HasValue && breakEvenHours.Value > config.BreakevenHoursMax)
                    {
                        diagnostics.PairsFilteredByBreakeven++;
                        continue;
                    }

                    // Trend analysis: check if funding spread has been favorable for N consecutive snapshots
                    historyLookup.TryGetValue((longR.AssetId, longR.ExchangeId), out var longHistory);
                    historyLookup.TryGetValue((shortR.AssetId, shortR.ExchangeId), out var shortHistory);

                    if (longHistory is not null && shortHistory is not null
                        && longHistory.Count >= config.MinConsecutiveFavorableCycles
                        && shortHistory.Count >= config.MinConsecutiveFavorableCycles)
                    {
                        var allFavorable = true;
                        for (int k = 0; k < config.MinConsecutiveFavorableCycles; k++)
                        {
                            if (k < longHistory.Count && k < shortHistory.Count)
                            {
                                if (shortHistory[k].RatePerHour - longHistory[k].RatePerHour <= 0)
                                {
                                    allFavorable = false;
                                    break;
                                }
                            }
                        }
                        if (!allFavorable)
                        {
                            dto.TrendUnconfirmed = true;
                        }
                    }
                    else
                    {
                        // Not enough history — mark as unconfirmed
                        dto.TrendUnconfirmed = true;
                    }

                    if (net >= config.OpenThreshold)
                    {
                        opportunities.Add(dto);
                    }
                    else if (net > 0)
                    {
                        netPositiveList.Add(dto);
                        diagnostics.NetPositiveBelowThreshold++;
                    }
                    else
                    {
                        diagnostics.PairsFilteredByThreshold++;
                    }
                }
            }
        }

        diagnostics.PairsPassing = opportunities.Count;
        var sorted = opportunities.OrderByDescending(o => o.NetYieldPerHour).Take(26).ToList();

        return new OpportunityResultDto
        {
            Opportunities = sorted,
            AllNetPositive = netPositiveList.OrderByDescending(o => o.NetYieldPerHour).Take(26).ToList(),
            Diagnostics = diagnostics
        };
    }
}
