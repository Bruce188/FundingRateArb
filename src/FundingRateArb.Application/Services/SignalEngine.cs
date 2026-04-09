using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class SignalEngine : ISignalEngine
{
    private readonly IUnitOfWork _uow;
    private readonly IMarketDataCache _cache;
    private readonly IRatePredictionService? _predictionService;
    private readonly ILeverageTierProvider? _tierProvider;
    private readonly ICoinGlassScreeningProvider? _screeningProvider;
    private readonly ILogger<SignalEngine>? _logger;

    public SignalEngine(
        IUnitOfWork uow,
        IMarketDataCache cache,
        IRatePredictionService? predictionService = null,
        ILeverageTierProvider? tierProvider = null,
        ICoinGlassScreeningProvider? screeningProvider = null,
        ILogger<SignalEngine>? logger = null)
    {
        _uow = uow;
        _cache = cache;
        _predictionService = predictionService;
        _tierProvider = tierProvider;
        _screeningProvider = screeningProvider;
        _logger = logger;
    }

    public async Task<List<ArbitrageOpportunityDto>> GetOpportunitiesAsync(CancellationToken ct = default)
    {
        var result = await GetOpportunitiesWithDiagnosticsAsync(ct);
        return result.Opportunities;
    }

    public async Task<OpportunityResultDto> GetOpportunitiesWithDiagnosticsAsync(CancellationToken ct = default)
    {
        BotConfiguration config;
        List<FundingRateSnapshot> latestRates;
        try
        {
            config = await _uow.BotConfig.GetActiveAsync();
            latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        }
        catch (DatabaseUnavailableException ex)
        {
            // Transient DB outage — return a degraded result so the dashboard can render
            // a banner instead of a 500 page. Callers that only need the opportunity
            // list will see an empty collection; callers that branch on DatabaseAvailable
            // can show a user-visible warning.
            _logger?.LogWarning(ex,
                "SignalEngine detected database unavailable; returning degraded result");
            return new OpportunityResultDto
            {
                IsSuccess = false,
                DatabaseAvailable = false,
                FailureReason = SignalEngineFailureReason.DatabaseUnavailable,
                Error = "database temporarily unavailable",
                Opportunities = [],
                AllNetPositive = [],
                Diagnostics = null,
            };
        }

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

        // Fetch CoinGlass pre-calculated arbitrage screening (non-critical — null on failure).
        // Used as a priority hint: symbols listed here are showing active cross-exchange arbitrage
        // on CoinGlass-tracked exchanges, so equivalent opportunities on directly-connected exchanges
        // should be preferred in ranking.
        IReadOnlySet<string>? hotSymbols = null;
        if (_screeningProvider is not null)
        {
            try
            {
                hotSymbols = await _screeningProvider.GetHotSymbolsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to load CoinGlass screening data; continuing without priority hints");
            }
        }

        // Lazy-load funding rate history for trend analysis (sequential — DbContext is not thread-safe).
        // Only queried for pairs that pass volume + break-even filters, avoiding unnecessary DB calls.
        var historyLookup = new Dictionary<(int AssetId, int ExchangeId), List<FundingRateSnapshot>>();
        // Scale lookback by max funding interval across all active exchanges.
        // Exchanges with 8h intervals (e.g. Aster) need 24h+ for MinConsecutiveFavorableCycles=3.
        var maxIntervalHours = rates
            .Where(r => r.Exchange is not null)
            .Select(r => r.Exchange!.FundingIntervalHours)
            .DefaultIfEmpty(1)
            .Max();
        var historyFrom = DateTime.UtcNow.AddHours(-maxIntervalHours * config.MinConsecutiveFavorableCycles);
        var historyTo = DateTime.UtcNow;

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
                        IsCoinGlassHot = hotSymbols is not null
                            && !string.IsNullOrEmpty(symbol)
                            && hotSymbols.Contains(symbol),
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

                    // Trend analysis: check if funding spread has been favorable for N consecutive snapshots.
                    // Load history on-demand (only for pairs that pass volume + break-even filters).
                    var longKey = (longR.AssetId, longR.ExchangeId);
                    if (!historyLookup.TryGetValue(longKey, out var longHistory))
                    {
                        longHistory = await _uow.FundingRates.GetHistoryAsync(
                            longR.AssetId, longR.ExchangeId, historyFrom, historyTo,
                            take: config.MinConsecutiveFavorableCycles);
                        historyLookup[longKey] = longHistory;
                    }
                    var shortKey = (shortR.AssetId, shortR.ExchangeId);
                    if (!historyLookup.TryGetValue(shortKey, out var shortHistory))
                    {
                        shortHistory = await _uow.FundingRates.GetHistoryAsync(
                            shortR.AssetId, shortR.ExchangeId, historyFrom, historyTo,
                            take: config.MinConsecutiveFavorableCycles);
                        historyLookup[shortKey] = shortHistory;
                    }

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

                    // MinEdgeThreshold per Appendix B: opportunity must cover at least
                    // MinEdgeMultiplier × amortized entry cost to justify execution. This is
                    // the industry best-practice safety guardrail — below 3× cost, noise,
                    // slippage, and funding flips can easily wipe out the edge during a
                    // realistic hold. The multiplier is configurable so operators can relax
                    // it in backtests or tighten it in production; default is 3× per spec.
                    var amortizedEntryCostPerHour = amortHours > 0 ? totalEntryCost / amortHours : totalEntryCost;
                    var minEdgeMultiplier = config.MinEdgeMultiplier > 0 ? config.MinEdgeMultiplier : 3m;
                    var minEdgeThreshold = minEdgeMultiplier * amortizedEntryCostPerHour;
                    var passesMinEdge = net >= minEdgeThreshold;

                    if (net >= config.OpenThreshold && passesMinEdge)
                    {
                        opportunities.Add(dto);
                    }
                    else if (net >= config.OpenThreshold)
                    {
                        // Passed OpenThreshold but failed the 3x edge guardrail — record
                        // in a dedicated counter so operators can see they need to loosen
                        // the multiplier, not the threshold.
                        netPositiveList.Add(dto);
                        diagnostics.NetPositiveBelowEdgeGuardrail++;
                    }
                    else if (net > 0)
                    {
                        // Net-positive but below OpenThreshold.
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
        // Sort: CoinGlass-hot symbols first as priority hint, then by net yield.
        // Matching hot symbols have corroborating evidence from CoinGlass's broader exchange
        // universe that active arbitrage exists, so they rank above equivalent non-hot yields.
        var sorted = opportunities
            .OrderByDescending(o => o.IsCoinGlassHot)
            .ThenByDescending(o => o.NetYieldPerHour)
            .Take(26)
            .ToList();

        return new OpportunityResultDto
        {
            Opportunities = sorted,
            AllNetPositive = netPositiveList.OrderByDescending(o => o.NetYieldPerHour).Take(26).ToList(),
            Diagnostics = diagnostics
        };
    }
}
