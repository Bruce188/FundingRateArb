using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public class RateAnalyticsService : IRateAnalyticsService
{
    private readonly IUnitOfWork _uow;

    // Z-score alert cache (5-minute TTL) — alerts change at most hourly
    private static List<ZScoreAlertDto>? _zScoreCache;
    private static decimal _zScoreCacheThreshold;
    private static DateTime _zScoreCacheExpiry = DateTime.MinValue;
    private static readonly object _zScoreCacheLock = new();

    public RateAnalyticsService(IUnitOfWork uow) => _uow = uow;

    public async Task<List<RateTrendDto>> GetRateTrendsAsync(int? assetId, int days = 7, int? exchangeId = null, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        // Fire independent DB queries concurrently — pass exchangeId to filter at DB level
        var aggregatesTask = _uow.FundingRates.GetHourlyAggregatesAsync(assetId, exchangeId, from, to, ct);
        var assetsTask = _uow.Assets.GetActiveAsync();
        var exchangesTask = _uow.Exchanges.GetActiveAsync();
        await Task.WhenAll(aggregatesTask, assetsTask, exchangesTask);

        var aggregates = aggregatesTask.Result;
        if (aggregates.Count == 0)
            return [];

        var assets = assetsTask.Result;
        var exchanges = exchangesTask.Result;
        var assetLookup = assets.ToDictionary(a => a.Id, a => a.Symbol);
        var exchangeLookup = exchanges.ToDictionary(e => e.Id, e => e.Name);

        // Group by (assetId, exchangeId)
        var groups = aggregates
            .GroupBy(a => new { a.AssetId, a.ExchangeId })
            .ToList();

        var results = new List<RateTrendDto>();
        foreach (var group in groups)
        {
            var orderedRates = group.OrderBy(a => a.HourUtc).ToList();
            if (orderedRates.Count == 0)
                continue;

            var hourlyPoints = orderedRates.Select(a => new HourlyRatePoint(
                a.HourUtc, a.AvgRatePerHour, a.MinRate, a.MaxRate, a.AvgVolume24hUsd)).ToList();

            var currentRate = orderedRates[orderedRates.Count - 1].AvgRatePerHour;
            var avgPeriod = orderedRates.Average(a => a.AvgRatePerHour);

            // 24h average: last 24 hours of data
            var cutoff24h = to.AddHours(-24);
            var last24h = orderedRates.Where(a => a.HourUtc >= cutoff24h).ToList();
            var avg24h = last24h.Count > 0 ? last24h.Average(a => a.AvgRatePerHour) : avgPeriod;

            // Trend: compare last 6h avg vs previous 6h avg
            var trendDirection = ComputeTrendDirection(orderedRates);

            var assetSymbol = assetLookup.GetValueOrDefault(group.Key.AssetId, $"#{group.Key.AssetId}");
            var exchangeName = exchangeLookup.GetValueOrDefault(group.Key.ExchangeId, $"#{group.Key.ExchangeId}");

            results.Add(new RateTrendDto(
                group.Key.AssetId, assetSymbol,
                group.Key.ExchangeId, exchangeName,
                currentRate, avgPeriod, avg24h,
                trendDirection, hourlyPoints));
        }

        return results;
    }

    public async Task<List<CorrelationPairDto>> GetCrossExchangeCorrelationAsync(int assetId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        // Fire independent DB queries concurrently
        var aggregatesTask = _uow.FundingRates.GetHourlyAggregatesAsync(assetId, null, from, to, ct);
        var exchangesTask = _uow.Exchanges.GetActiveAsync();
        await Task.WhenAll(aggregatesTask, exchangesTask);

        var aggregates = aggregatesTask.Result;
        if (aggregates.Count == 0)
            return [];

        var exchanges = exchangesTask.Result;
        var exchangeLookup = exchanges.ToDictionary(e => e.Id, e => e.Name);

        // Build a sorted set of all unique hours and per-exchange rate arrays aligned to the same time axis.
        // This avoids repeated Intersect + LINQ allocations for each exchange pair.
        var byExchange = aggregates
            .GroupBy(a => a.ExchangeId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(a => a.HourUtc, a => a.AvgRatePerHour));

        var exchangeIds = byExchange.Keys.OrderBy(id => id).ToList();
        var allHours = byExchange.Values
            .SelectMany(d => d.Keys)
            .Distinct()
            .OrderBy(h => h)
            .ToList();

        // Build a matrix: exchangeId → sparse array indexed by allHours position
        var hourIndex = new Dictionary<DateTime, int>(allHours.Count);
        for (int h = 0; h < allHours.Count; h++)
            hourIndex[allHours[h]] = h;

        var rateMatrix = new Dictionary<int, decimal?[]>(exchangeIds.Count);
        foreach (var exId in exchangeIds)
        {
            var arr = new decimal?[allHours.Count];
            foreach (var (hour, rate) in byExchange[exId])
                arr[hourIndex[hour]] = rate;
            rateMatrix[exId] = arr;
        }

        var results = new List<CorrelationPairDto>();

        for (int i = 0; i < exchangeIds.Count; i++)
        {
            for (int j = i + 1; j < exchangeIds.Count; j++)
            {
                var ex1 = exchangeIds[i];
                var ex2 = exchangeIds[j];
                var arr1 = rateMatrix[ex1];
                var arr2 = rateMatrix[ex2];

                // Extract overlapping values directly from aligned arrays
                var series1 = new List<decimal>();
                var series2 = new List<decimal>();
                for (int h = 0; h < allHours.Count; h++)
                {
                    if (arr1[h].HasValue && arr2[h].HasValue)
                    {
                        series1.Add(arr1[h]!.Value);
                        series2.Add(arr2[h]!.Value);
                    }
                }

                if (series1.Count < 2)
                    continue;

                var pearsonR = ComputePearsonCorrelation(series1, series2);

                var name1 = exchangeLookup.GetValueOrDefault(ex1, $"#{ex1}");
                var name2 = exchangeLookup.GetValueOrDefault(ex2, $"#{ex2}");

                results.Add(new CorrelationPairDto(name1, name2, pearsonR, series1.Count));
            }
        }

        return results;
    }

    public async Task<List<TimeOfDayPatternDto>> GetTimeOfDayPatternsAsync(int assetId, int exchangeId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        var aggregates = await _uow.FundingRates.GetHourlyAggregatesAsync(assetId, exchangeId, from, to, ct);
        if (aggregates.Count == 0)
            return [];

        // Group by hour-of-day (0-23)
        var byHour = aggregates
            .GroupBy(a => a.HourUtc.Hour)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var rates = g.Select(a => a.AvgRatePerHour).ToList();
                var avg = rates.Average();
                var stdDev = ComputeStdDev(rates, avg);
                return new TimeOfDayPatternDto(g.Key, avg, stdDev, rates.Count);
            })
            .ToList();

        return byHour;
    }

    public async Task<List<ZScoreAlertDto>> GetZScoreAlertsAsync(decimal threshold = 2.0m, CancellationToken ct = default)
    {
        // Return cached results if within 5-minute TTL and same threshold
        lock (_zScoreCacheLock)
        {
            if (_zScoreCache is not null
                && _zScoreCacheThreshold == threshold
                && DateTime.UtcNow < _zScoreCacheExpiry)
            {
                return _zScoreCache;
            }
        }

        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;

        // Fire all independent DB queries concurrently — stats computed via SQL aggregation
        var latestRatesTask = _uow.FundingRates.GetLatestAggregatePerAssetExchangeAsync(ct);
        var statsTask = _uow.FundingRates.GetAggregateStatsByPairAsync(from, to, ct);
        var assetsTask = _uow.Assets.GetActiveAsync();
        var exchangesTask = _uow.Exchanges.GetActiveAsync();

        await Task.WhenAll(latestRatesTask, statsTask, assetsTask, exchangesTask);

        var latestRates = await latestRatesTask;
        if (latestRates.Count == 0)
            return [];

        var stats = await statsTask;
        var statsByPair = stats.ToDictionary(s => (s.AssetId, s.ExchangeId));

        var assets = await assetsTask;
        var exchanges = await exchangesTask;
        var assetLookup = assets.ToDictionary(a => a.Id, a => a.Symbol);
        var exchangeLookup = exchanges.ToDictionary(e => e.Id, e => e.Name);

        var results = new List<ZScoreAlertDto>();

        foreach (var latest in latestRates)
        {
            var key = (latest.AssetId, latest.ExchangeId);
            if (!statsByPair.TryGetValue(key, out var pairStats))
                continue;

            if (pairStats.StdDev == 0)
                continue;

            var zScore = (latest.AvgRatePerHour - pairStats.Mean) / pairStats.StdDev;

            if (Math.Abs(zScore) >= threshold)
            {
                var assetSymbol = assetLookup.GetValueOrDefault(latest.AssetId, $"#{latest.AssetId}");
                var exchangeName = exchangeLookup.GetValueOrDefault(latest.ExchangeId, $"#{latest.ExchangeId}");

                results.Add(new ZScoreAlertDto(
                    assetSymbol, exchangeName,
                    latest.AvgRatePerHour, pairStats.Mean, pairStats.StdDev, zScore));
            }
        }

        var capped = results
            .OrderByDescending(a => Math.Abs(a.ZScore))
            .Take(100)
            .ToList();

        // Cache for 5 minutes — Z-scores from hourly data change at most hourly
        lock (_zScoreCacheLock)
        {
            _zScoreCache = capped;
            _zScoreCacheThreshold = threshold;
            _zScoreCacheExpiry = DateTime.UtcNow.AddMinutes(5);
        }

        return capped;
    }

    public static string ComputeTrendDirection(List<FundingRateHourlyAggregate> orderedRates)
    {
        if (orderedRates.Count < 2)
            return "stable";

        // Compare last 6h avg vs previous 6h avg
        var totalCount = orderedRates.Count;
        var halfSize = Math.Min(6, totalCount / 2);

        if (halfSize == 0)
            return "stable";

        var recentAvg = orderedRates
            .Skip(totalCount - halfSize)
            .Average(a => a.AvgRatePerHour);
        var previousAvg = orderedRates
            .Skip(Math.Max(0, totalCount - 2 * halfSize))
            .Take(halfSize)
            .Average(a => a.AvgRatePerHour);

        if (previousAvg == 0)
            return recentAvg > 0 ? "rising" : recentAvg < 0 ? "falling" : "stable";

        var change = (recentAvg - previousAvg) / Math.Abs(previousAvg);

        return change >= 0.10m ? "rising"
            : change <= -0.10m ? "falling"
            : "stable";
    }

    public static decimal ComputePearsonCorrelation(List<decimal> x, List<decimal> y)
    {
        if (x.Count != y.Count || x.Count < 2)
            return 0m;

        var n = x.Count;
        var avgX = x.Average();
        var avgY = y.Average();

        decimal sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < n; i++)
        {
            var dx = x[i] - avgX;
            var dy = y[i] - avgY;
            sumXY += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }

        if (sumX2 == 0 || sumY2 == 0)
            return 0m;

        // Cast to double before multiplying to avoid decimal overflow on large series
        var denominator = (double)sumX2 * (double)sumY2;
        return (decimal)((double)sumXY / Math.Sqrt(denominator));
    }

    public static decimal ComputeStdDev(List<decimal> values, decimal mean)
    {
        if (values.Count < 2)
            return 0m;

        var sumSquaredDiffs = values.Sum(v => (v - mean) * (v - mean));
        return (decimal)Math.Sqrt((double)(sumSquaredDiffs / (values.Count - 1)));
    }

    /// <summary>
    /// Clears the Z-score alert cache. Used for testing.
    /// </summary>
    public static void ResetZScoreCache()
    {
        lock (_zScoreCacheLock)
        {
            _zScoreCache = null;
            _zScoreCacheExpiry = DateTime.MinValue;
        }
    }
}
