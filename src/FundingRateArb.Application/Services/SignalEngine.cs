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
    private readonly ILogger<SignalEngine>? _logger;

    /// <summary>
    /// Fallback round-trip fee constants used when Exchange.TakerFeeRate is not set in the DB.
    /// </summary>
    private static readonly Dictionary<string, decimal> FallbackRoundTripFees = new()
    {
        { "Hyperliquid", 0.00090m },
        { "Lighter",     0.00000m },
        { "Aster",       0.00080m }
    };

    public SignalEngine(IUnitOfWork uow, IMarketDataCache cache, IRatePredictionService? predictionService = null, ILogger<SignalEngine>? logger = null)
    {
        _uow = uow;
        _cache = cache;
        _predictionService = predictionService;
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
                predictionLookup = predictions.ToDictionary(p => (p.AssetId, p.ExchangeId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Predictions are informational — don't block opportunity generation
                _logger?.LogTrace(ex, "Failed to load rate predictions; continuing without prediction data");
            }
        }

        var opportunities = new List<ArbitrageOpportunityDto>();
        var netPositiveList = new List<ArbitrageOpportunityDto>();

        foreach (var group in rates.Where(r => r.Asset?.Symbol is not null).GroupBy(r => r.Asset!.Symbol))
        {
            var symbol    = group.Key;
            var assetRates = group.ToList();

            for (int i = 0; i < assetRates.Count; i++)
            for (int j = i + 1; j < assetRates.Count; j++)
            {
                var a = assetRates[i];
                var b = assetRates[j];

                var (longR, shortR) = a.RatePerHour <= b.RatePerHour ? (a, b) : (b, a);

                diagnostics.TotalPairsEvaluated++;

                var diff = shortR.RatePerHour - longR.RatePerHour;

                // Track best raw spread across ALL pairs, before any filter
                if (diff > diagnostics.BestRawSpread)
                    diagnostics.BestRawSpread = diff;

                // M2: Skip opportunities where either leg has insufficient volume
                if (longR.Volume24hUsd < config.MinVolume24hUsdc || shortR.Volume24hUsd < config.MinVolume24hUsdc)
                {
                    diagnostics.PairsFilteredByVolume++;
                    continue;
                }

                // Use DB-stored TakerFeeRate when available; fall back to built-in constants.
                var longFee  = longR.Exchange.TakerFeeRate * 2
                               ?? FallbackRoundTripFees.GetValueOrDefault(longR.Exchange.Name, 0.001m);
                var shortFee = shortR.Exchange.TakerFeeRate * 2
                               ?? FallbackRoundTripFees.GetValueOrDefault(shortR.Exchange.Name, 0.001m);
                var amortHours = Math.Max(config.FeeAmortizationHours, 1);
                var feePerHour = (longFee + shortFee) / amortHours;
                var net = diff - feePerHour;

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
                    if (minutesToSettlement == int.MaxValue) minutesToSettlement = null;
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
                    NetYieldPerHour = boostedNet,
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
                };

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
