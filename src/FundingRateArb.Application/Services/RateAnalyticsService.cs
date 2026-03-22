using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public class RateAnalyticsService : IRateAnalyticsService
{
    private readonly IUnitOfWork _uow;

    public RateAnalyticsService(IUnitOfWork uow) => _uow = uow;

    public async Task<List<RateTrendDto>> GetRateTrendsAsync(int? assetId, int days = 7, CancellationToken ct = default)
    {
        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        var aggregates = await _uow.FundingRates.GetHourlyAggregatesAsync(assetId, null, from, to, ct);
        if (aggregates.Count == 0)
            return [];

        // Load active assets and exchanges for symbol/name resolution (consistent with controller dropdowns)
        var assets = await _uow.Assets.GetActiveAsync();
        var exchanges = await _uow.Exchanges.GetActiveAsync();
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
            var hourlyPoints = orderedRates.Select(a => new HourlyRatePoint(
                a.HourUtc, a.AvgRatePerHour, a.MinRate, a.MaxRate, a.AvgVolume24hUsd)).ToList();

            var currentRate = orderedRates.Last().AvgRatePerHour;
            var avg7d = orderedRates.Average(a => a.AvgRatePerHour);

            // 24h average: last 24 hours of data
            var cutoff24h = to.AddHours(-24);
            var last24h = orderedRates.Where(a => a.HourUtc >= cutoff24h).ToList();
            var avg24h = last24h.Count > 0 ? last24h.Average(a => a.AvgRatePerHour) : avg7d;

            // Trend: compare last 6h avg vs previous 6h avg
            var trendDirection = ComputeTrendDirection(orderedRates);

            var assetSymbol = assetLookup.GetValueOrDefault(group.Key.AssetId, $"#{group.Key.AssetId}");
            var exchangeName = exchangeLookup.GetValueOrDefault(group.Key.ExchangeId, $"#{group.Key.ExchangeId}");

            results.Add(new RateTrendDto(
                group.Key.AssetId, assetSymbol,
                group.Key.ExchangeId, exchangeName,
                currentRate, avg7d, avg24h,
                trendDirection, hourlyPoints));
        }

        return results;
    }

    public async Task<List<CorrelationPairDto>> GetCrossExchangeCorrelationAsync(int assetId, int days = 7, CancellationToken ct = default)
    {
        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        var aggregates = await _uow.FundingRates.GetHourlyAggregatesAsync(assetId, null, from, to, ct);
        if (aggregates.Count == 0)
            return [];

        var exchanges = await _uow.Exchanges.GetActiveAsync();
        var exchangeLookup = exchanges.ToDictionary(e => e.Id, e => e.Name);

        // Group by exchange, keyed by HourUtc
        var byExchange = aggregates
            .GroupBy(a => a.ExchangeId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(a => a.HourUtc, a => a.AvgRatePerHour));

        var exchangeIds = byExchange.Keys.OrderBy(id => id).ToList();
        var results = new List<CorrelationPairDto>();

        for (int i = 0; i < exchangeIds.Count; i++)
        {
            for (int j = i + 1; j < exchangeIds.Count; j++)
            {
                var ex1 = exchangeIds[i];
                var ex2 = exchangeIds[j];
                var rates1 = byExchange[ex1];
                var rates2 = byExchange[ex2];

                // Find overlapping hours
                var commonHours = rates1.Keys.Intersect(rates2.Keys).OrderBy(h => h).ToList();
                if (commonHours.Count < 2)
                    continue;

                var series1 = commonHours.Select(h => rates1[h]).ToList();
                var series2 = commonHours.Select(h => rates2[h]).ToList();

                var pearsonR = ComputePearsonCorrelation(series1, series2);

                var name1 = exchangeLookup.GetValueOrDefault(ex1, $"#{ex1}");
                var name2 = exchangeLookup.GetValueOrDefault(ex2, $"#{ex2}");

                results.Add(new CorrelationPairDto(name1, name2, pearsonR, commonHours.Count));
            }
        }

        return results;
    }

    public async Task<List<TimeOfDayPatternDto>> GetTimeOfDayPatternsAsync(int assetId, int exchangeId, int days = 7, CancellationToken ct = default)
    {
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
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;

        // Fire all independent DB queries concurrently
        var latestRatesTask = _uow.FundingRates.GetLatestAggregatePerAssetExchangeAsync(ct);
        var allAggregatesTask = _uow.FundingRates.GetHourlyAggregatesAsync(null, null, from, to, ct);
        var assetsTask = _uow.Assets.GetActiveAsync();
        var exchangesTask = _uow.Exchanges.GetActiveAsync();

        await Task.WhenAll(latestRatesTask, allAggregatesTask, assetsTask, exchangesTask);

        var latestRates = await latestRatesTask;
        if (latestRates.Count == 0)
            return [];

        var allAggregates = await allAggregatesTask;
        var historicalByPair = allAggregates
            .GroupBy(a => (a.AssetId, a.ExchangeId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.AvgRatePerHour).ToList());

        var assets = await assetsTask;
        var exchanges = await exchangesTask;
        var assetLookup = assets.ToDictionary(a => a.Id, a => a.Symbol);
        var exchangeLookup = exchanges.ToDictionary(e => e.Id, e => e.Name);

        var results = new List<ZScoreAlertDto>();

        foreach (var latest in latestRates)
        {
            var key = (latest.AssetId, latest.ExchangeId);
            if (!historicalByPair.TryGetValue(key, out var historicalRates) || historicalRates.Count < 2)
                continue;

            var mean = historicalRates.Average();
            var stdDev = ComputeStdDev(historicalRates, mean);

            if (stdDev == 0)
                continue;

            var zScore = (latest.AvgRatePerHour - mean) / stdDev;

            if (Math.Abs(zScore) >= threshold)
            {
                var assetSymbol = assetLookup.GetValueOrDefault(latest.AssetId, $"#{latest.AssetId}");
                var exchangeName = exchangeLookup.GetValueOrDefault(latest.ExchangeId, $"#{latest.ExchangeId}");

                results.Add(new ZScoreAlertDto(
                    assetSymbol, exchangeName,
                    latest.AvgRatePerHour, mean, stdDev, zScore));
            }
        }

        return results.OrderByDescending(a => Math.Abs(a.ZScore)).ToList();
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

        var denominator = (double)(sumX2 * sumY2);
        return (decimal)((double)sumXY / Math.Sqrt(denominator));
    }

    public static decimal ComputeStdDev(List<decimal> values, decimal mean)
    {
        if (values.Count < 2)
            return 0m;

        var sumSquaredDiffs = values.Sum(v => (v - mean) * (v - mean));
        return (decimal)Math.Sqrt((double)(sumSquaredDiffs / (values.Count - 1)));
    }
}
