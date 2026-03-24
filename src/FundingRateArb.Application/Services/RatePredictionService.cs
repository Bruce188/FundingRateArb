using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Application.Services;

public class RatePredictionService : IRatePredictionService
{
    private const decimal Alpha = 0.3m;
    private const int MinHoursRequired = 24;
    private const int FetchHours = 72;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKey = "RatePredictions_All";

    private readonly IUnitOfWork _uow;
    private readonly IMemoryCache _cache;

    public RatePredictionService(IUnitOfWork uow, IMemoryCache cache)
    {
        _uow = uow;
        _cache = cache;
    }

    public async Task<List<RatePredictionDto>> GetPredictionsAsync(CancellationToken ct = default)
    {
        // F4: Cache predictions with 5-min TTL — hourly aggregates change at most once per hour
        if (_cache.TryGetValue(CacheKey, out List<RatePredictionDto>? cached) && cached is not null)
        {
            return cached;
        }

        var from = DateTime.UtcNow.AddHours(-FetchHours);
        var to = DateTime.UtcNow;

        // F8: Fetches all aggregates across all pairs for 72h. Expected upper bound:
        // ~20 assets x ~3 exchanges x 72 hours = ~4,320 rows. Acceptable for in-memory processing.
        // If pair count grows unbounded, consider server-side aggregation or batch by asset.
        var aggregates = await _uow.FundingRates.GetHourlyAggregatesAsync(null, null, from, to, ct);
        if (aggregates.Count == 0)
        {
            return [];
        }

        // F17: Use GetActiveAsync instead of GetAllAsync to skip inactive/soft-deleted records
        var assets = await _uow.Assets.GetActiveAsync();
        var exchanges = await _uow.Exchanges.GetActiveAsync();
        var assetLookup = assets.ToDictionary(a => a.Id, a => a.Symbol);
        var exchangeLookup = exchanges.ToDictionary(e => e.Id, e => e.Name);

        // Pre-sort once, then group — avoids redundant OrderBy inside ComputePrediction
        var ordered = aggregates.OrderBy(a => a.HourUtc).ToList();
        var groups = ordered.GroupBy(a => new { a.AssetId, a.ExchangeId });

        var results = new List<RatePredictionDto>();

        foreach (var group in groups)
        {
            // Pass group directly as List — data is already sorted chronologically
            var groupList = group.ToList();
            var prediction = ComputePrediction(groupList,
                group.Key.AssetId, group.Key.ExchangeId,
                assetLookup, exchangeLookup);
            if (prediction is not null)
            {
                results.Add(prediction);
            }
        }

        _cache.Set(CacheKey, results, CacheDuration);
        return results;
    }

    public async Task<RatePredictionDto?> GetPredictionAsync(int assetId, int exchangeId, CancellationToken ct = default)
    {
        // F14: Check bulk cache first to avoid redundant DB queries
        if (_cache.TryGetValue(CacheKey, out List<RatePredictionDto>? cached) && cached is not null)
        {
            return cached.FirstOrDefault(p => p.AssetId == assetId && p.ExchangeId == exchangeId);
        }

        var from = DateTime.UtcNow.AddHours(-FetchHours);
        var to = DateTime.UtcNow;

        var aggregates = await _uow.FundingRates.GetHourlyAggregatesAsync(assetId, exchangeId, from, to, ct);
        if (aggregates.Count < MinHoursRequired)
        {
            return null;
        }

        var asset = await _uow.Assets.GetByIdAsync(assetId);
        var exchange = await _uow.Exchanges.GetByIdAsync(exchangeId);
        var assetLookup = new Dictionary<int, string>();
        var exchangeLookup = new Dictionary<int, string>();
        if (asset is not null)
        {
            assetLookup[assetId] = asset.Symbol;
        }

        if (exchange is not null)
        {
            exchangeLookup[exchangeId] = exchange.Name;
        }

        return ComputePrediction(aggregates, assetId, exchangeId, assetLookup, exchangeLookup);
    }

    public static RatePredictionDto? ComputePrediction(
        List<FundingRateHourlyAggregate> aggregates,
        int assetId, int exchangeId,
        Dictionary<int, string> assetLookup,
        Dictionary<int, string> exchangeLookup)
    {
        // Caller is responsible for providing chronologically ordered data.
        // Fall back to sorting only if needed (single-pair queries from GetPredictionAsync).
        var ordered = aggregates;
        if (aggregates.Count > 1 && aggregates[0].HourUtc > aggregates[^1].HourUtc)
        {
            ordered = aggregates.OrderBy(a => a.HourUtc).ToList();
        }

        if (ordered.Count < MinHoursRequired)
        {
            return null;
        }

        // Compute EWMA and one-step-ahead forecast
        var rates = ordered.Select(a => a.AvgRatePerHour).ToList();
        var ewmaValues = ComputeEwma(rates);
        // One-step-ahead forecast: project beyond the last observation
        var predictedRate = Alpha * rates[^1] + (1 - Alpha) * ewmaValues[^1];

        // Confidence calculation
        var confidence = ComputeConfidence(rates);

        // Trend: compare current EWMA vs EWMA at -12h (or halfway if fewer points)
        var trendDirection = ComputeTrend(ewmaValues);

        var assetSymbol = assetLookup.GetValueOrDefault(assetId, $"#{assetId}");
        var exchangeName = exchangeLookup.GetValueOrDefault(exchangeId, $"#{exchangeId}");

        return new RatePredictionDto(
            assetId, assetSymbol,
            exchangeId, exchangeName,
            predictedRate, confidence, trendDirection);
    }

    public static List<decimal> ComputeEwma(List<decimal> rates)
    {
        if (rates.Count == 0)
        {
            return [];
        }

        var ewma = new List<decimal>(rates.Count) { rates[0] };
        for (int i = 1; i < rates.Count; i++)
        {
            ewma.Add(Alpha * rates[i] + (1 - Alpha) * ewma[i - 1]);
        }
        return ewma;
    }

    public static decimal ComputeConfidence(List<decimal> rates)
    {
        if (rates.Count == 0)
        {
            return 0m;
        }

        var sampleCount = rates.Count;
        var avgRate = rates.Average();

        // Start at 1.0
        var confidence = 1.0m;

        // Penalize low sample count (need at least 48h for full confidence)
        confidence *= Math.Min((decimal)sampleCount / 48m, 1.0m);

        // F8: Apply floor to avoid division blow-up when avgRate is near zero
        var absAvg = Math.Max(Math.Abs(avgRate), 0.0001m);
        var stdDev = RateAnalyticsService.ComputeStdDev(rates, avgRate);
        var volatilityPenalty = 1.0m - Math.Min(stdDev / absAvg, 1.0m);
        confidence *= volatilityPenalty;

        return Math.Clamp(confidence, 0m, 1.0m);
    }

    public static string ComputeTrend(List<decimal> ewmaValues)
    {
        if (ewmaValues.Count < 2)
        {
            return "stable";
        }

        var currentEwma = ewmaValues[^1];

        // Compare to EWMA at -12 hours, or halfway point if fewer
        var compareIndex = Math.Max(0, ewmaValues.Count - 13);
        var previousEwma = ewmaValues[compareIndex];

        if (previousEwma == 0)
        {
            return currentEwma > 0 ? "rising" : currentEwma < 0 ? "falling" : "stable";
        }

        var change = (currentEwma - previousEwma) / Math.Abs(previousEwma);

        return change >= 0.15m ? "rising"
            : change <= -0.15m ? "falling"
            : "stable";
    }
}
