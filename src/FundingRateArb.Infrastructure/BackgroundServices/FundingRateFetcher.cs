using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class FundingRateFetcher : BackgroundService
{
    // M-FR3: Named constant instead of magic number
    private const int FetchIntervalSeconds = 60;

    // H-FR1: Track last purge time to run hourly purge without a separate timer
    private DateTime _lastPurgeUtc = DateTime.MinValue;

    // Track last aggregation hour to avoid re-aggregating
    private DateTime _lastAggregatedHourUtc = DateTime.MinValue;

    // Track the last cycle time for settlement boundary detection on periodic exchanges.
    // Key: exchangeId, Value: last time we checked (used to detect if a settlement boundary was crossed).
    private readonly ConcurrentDictionary<int, DateTime> _lastCycleTimePerExchange = new();

    private static readonly TimeSpan CacheStaleThreshold = TimeSpan.FromSeconds(90);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMarketDataCache _cache;
    private readonly IFundingRateReadinessSignal _readinessSignal;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hubContext;
    private readonly ILogger<FundingRateFetcher> _logger;
    private bool _hasSignaled;

    public FundingRateFetcher(
        IServiceScopeFactory scopeFactory,
        IMarketDataCache cache,
        IFundingRateReadinessSignal readinessSignal,
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        ILogger<FundingRateFetcher> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _readinessSignal = readinessSignal;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // M-FR3: Run once immediately so the dashboard has data on startup (no 60s blank wait)
        try
        {
            await FetchAllAsync(ct);
            SignalReadyOnce();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial funding rate fetch failed");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(FetchIntervalSeconds));

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await FetchAllAsync(ct);
                SignalReadyOnce();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Funding rate fetch cycle failed");
            }
        }
    }

    /// <summary>
    /// Fetches rates from all active exchanges and persists them as FundingRateSnapshot rows.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task FetchAllAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var factory = scope.ServiceProvider.GetRequiredService<IExchangeConnectorFactory>();

        var connectors = factory.GetAllConnectors().ToList();

        // Hybrid: use WebSocket cache when fresh, fall back to REST when stale
        var allRates = new List<FundingRateDto>();
        foreach (var c in connectors)
        {
            var cached = _cache.GetAllForExchange(c.ExchangeName);
            if (cached.Count > 0 && !_cache.IsStaleForExchange(c.ExchangeName, CacheStaleThreshold))
            {
                _logger.LogDebug("Using WebSocket cache for {Exchange} ({Count} rates)", c.ExchangeName, cached.Count);
                allRates.AddRange(cached);
            }
            else
            {
                _logger.LogDebug("Cache stale/empty for {Exchange}, falling back to REST", c.ExchangeName);
                try
                {
                    var rates = await c.GetFundingRatesAsync(ct);
                    _logger.LogDebug("Fetched {Count} rates from {Exchange} via REST", rates.Count, c.ExchangeName);
                    allRates.AddRange(rates);

                    // Seed cache with REST data so volume survives subsequent WS updates
                    foreach (var rate in rates)
                    {
                        _cache.Update(rate);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "REST fallback failed for {Exchange}", c.ExchangeName);
                }
            }
        }

        // Resolve exchange and asset lookup tables once
        var exchanges = await uow.Exchanges.GetActiveAsync();
        var assets = await uow.Assets.GetActiveAsync();

        // H-FR2: Build lookup dictionaries before the loop — O(N+M) instead of O(N*M)
        var exchangeMap = exchanges.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var assetMap = assets.ToDictionary(a => a.Symbol, StringComparer.OrdinalIgnoreCase);

        // M9: Build snapshot list first, then AddRange once (instead of individual Add calls)
        var now = DateTime.UtcNow;
        var snapshots = new List<FundingRateSnapshot>();

        foreach (var rate in allRates)
        {
            if (!exchangeMap.TryGetValue(rate.ExchangeName, out var exchange))
            {
                continue;
            }

            if (!assetMap.TryGetValue(rate.Symbol, out var asset))
            {
                continue;
            }

            snapshots.Add(new FundingRateSnapshot
            {
                ExchangeId = exchange.Id,
                AssetId = asset.Id,
                RatePerHour = rate.RatePerHour,
                RawRate = rate.RawRate,
                MarkPrice = rate.MarkPrice,
                IndexPrice = rate.IndexPrice,
                Volume24hUsd = rate.Volume24hUsd,
                RecordedAt = now,
            });
        }

        uow.FundingRates.AddRange(snapshots);
        await uow.SaveAsync(ct);

        _logger.LogInformation(
            "Funding rates saved: {Total} snapshots from {ExchangeCount} exchanges",
            snapshots.Count, connectors.Count);

        // H-FR1: Hourly purge — keep only 48h of data to prevent unbounded table growth
        if (DateTime.UtcNow - _lastPurgeUtc >= TimeSpan.FromHours(1))
        {
            var cutoff = DateTime.UtcNow.AddHours(-48);
            var purged = await uow.FundingRates.PurgeOlderThanAsync(cutoff, ct);
            _lastPurgeUtc = DateTime.UtcNow;
            _logger.LogInformation("Purged {Count} funding rate snapshots older than {Cutoff:u}", purged, cutoff);
        }

        // Hourly aggregation: at minute >= 5 of each hour, aggregate the previous hour's raw snapshots
        await TryAggregateHourlyAsync(uow, ct);

        await UpdateAccumulatedFundingAsync(uow, ct);

        // M13: Push live updates consistently to Group("MarketData"), not Clients.All
        // H8: Opportunity computation and push moved to BotOrchestrator (SRP — fetcher only fetches)
        await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveFundingRateUpdate(allRates);
    }

    /// <summary>
    /// At minute >= 5 of each hour, aggregates the previous hour's raw funding rate snapshots
    /// into hourly aggregates for 30-day extended retention. Also purges aggregates older than 30 days.
    /// </summary>
    internal async Task TryAggregateHourlyAsync(IUnitOfWork uow, CancellationToken ct, DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.UtcNow;
        if (now.Minute < 5)
        {
            return; // Wait until minute 5 to ensure all :00 snapshots are persisted
        }

        // Determine the hour we should aggregate (the previous completed hour)
        var previousHourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);

        // Skip if we already aggregated this hour
        if (previousHourStart <= _lastAggregatedHourUtc)
        {
            return;
        }

        var currentHourStart = previousHourStart.AddHours(1);
        var snapshots = await uow.FundingRates.GetSnapshotsInRangeAsync(previousHourStart, currentHourStart, ct);

        if (snapshots.Count == 0)
        {
            _lastAggregatedHourUtc = previousHourStart;
            return;
        }

        var aggregates = snapshots
            .GroupBy(s => new { s.ExchangeId, s.AssetId })
            .Select(g => new Domain.Entities.FundingRateHourlyAggregate
            {
                ExchangeId = g.Key.ExchangeId,
                AssetId = g.Key.AssetId,
                HourUtc = previousHourStart,
                AvgRatePerHour = g.Average(s => s.RatePerHour),
                MinRate = g.Min(s => s.RatePerHour),
                MaxRate = g.Max(s => s.RatePerHour),
                LastRate = g.OrderByDescending(s => s.RecordedAt).First().RatePerHour,
                AvgVolume24hUsd = g.Average(s => s.Volume24hUsd),
                AvgMarkPrice = g.Average(s => s.MarkPrice),
                SampleCount = g.Count(),
            })
            .ToList();

        try
        {
            uow.FundingRates.AddAggregateRange(aggregates);
            await uow.SaveAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Aggregates already exist for hour {Hour:u}, skipping insertion", previousHourStart);
            _lastAggregatedHourUtc = previousHourStart;
            return;
        }

        // Purge aggregates older than 30 days (idempotent, always safe)
        var aggregateCutoff = now.AddDays(-30);
        var purgedAggregates = await uow.FundingRates.PurgeAggregatesOlderThanAsync(aggregateCutoff, ct);
        await uow.SaveAsync(ct);

        _lastAggregatedHourUtc = previousHourStart;

        _logger.LogInformation(
            "Hourly aggregation: {Count} aggregates for {Hour:u} ({Samples} snapshots). Purged {Purged} old aggregates.",
            aggregates.Count, previousHourStart, snapshots.Count, purgedAggregates);
    }

    private async Task UpdateAccumulatedFundingAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var openPositions = await uow.Positions.GetOpenTrackedAsync();
        if (openPositions.Count == 0)
        {
            return;
        }

        var latestRates = await uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        var exchanges = await uow.Exchanges.GetActiveAsync();
        var exchangeById = exchanges.ToDictionary(e => e.Id);

        var now = DateTime.UtcNow;

        foreach (var pos in openPositions)
        {
            var longRate = latestRates
                .FirstOrDefault(r => r.ExchangeId == pos.LongExchangeId && r.AssetId == pos.AssetId);
            var shortRate = latestRates
                .FirstOrDefault(r => r.ExchangeId == pos.ShortExchangeId && r.AssetId == pos.AssetId);

            if (longRate is null || shortRate is null)
            {
                continue;
            }

            var notional = pos.SizeUsdc * pos.Leverage;

            // Compute each leg's funding contribution separately to handle mixed settlement types.
            // Convention: short leg earns funding (positive), long leg pays funding (negative).
            var longFunding = ComputeLegFunding(
                longRate.RatePerHour, notional, pos.LongExchangeId, exchangeById, now);
            var shortFunding = ComputeLegFunding(
                shortRate.RatePerHour, notional, pos.ShortExchangeId, exchangeById, now);

            // Net funding = short income - long cost
            pos.AccumulatedFunding += shortFunding - longFunding;
        }

        // Update last cycle time for all exchanges involved
        foreach (var exchange in exchanges)
        {
            _lastCycleTimePerExchange[exchange.Id] = now;
        }

        await uow.SaveAsync(ct);
    }

    /// <summary>
    /// Computes one leg's funding delta for this cycle.
    /// Continuous exchanges: pro-rata per minute (1/60 of hourly rate).
    /// Periodic exchanges: full interval's funding only when a settlement boundary is crossed,
    /// otherwise zero (funding has not been paid yet).
    /// </summary>
    internal decimal ComputeLegFunding(
        decimal ratePerHour,
        decimal notional,
        int exchangeId,
        Dictionary<int, Exchange> exchangeById,
        DateTime now)
    {
        if (!exchangeById.TryGetValue(exchangeId, out var exchange))
        {
            return 0m;
        }

        if (exchange.FundingSettlementType == FundingSettlementType.Periodic)
        {
            // For periodic exchanges, funding is settled at interval boundaries.
            // Detect if a settlement boundary has been crossed since the last cycle.
            var intervalHours = exchange.FundingIntervalHours;
            if (intervalHours <= 0)
            {
                intervalHours = 8; // safety fallback
            }

            var lastCycle = _lastCycleTimePerExchange.GetValueOrDefault(exchangeId, DateTime.MinValue);

            if (lastCycle == DateTime.MinValue)
            {
                // First cycle — no prior reference. Don't add a lump sum on startup
                // to avoid double-counting. Start tracking from this point.
                return 0m;
            }

            // Check if a settlement boundary was crossed between lastCycle and now.
            // Settlement times: hours where hour % intervalHours == 0 (UTC).
            var lastSettlement = FloorToSettlement(lastCycle, intervalHours);
            var currentSettlement = FloorToSettlement(now, intervalHours);

            if (currentSettlement > lastSettlement)
            {
                // A settlement boundary was crossed. Add the full interval's funding.
                // The rate is already normalised to per-hour, so the interval payment =
                // notional * ratePerHour * intervalHours.
                var intervalFunding = notional * ratePerHour * intervalHours;
                _logger.LogDebug(
                    "Periodic settlement crossed for exchange {ExchangeId}: added {Funding:F6} (rate={Rate}, interval={Hours}h)",
                    exchangeId, intervalFunding, ratePerHour, intervalHours);
                return intervalFunding;
            }

            // No settlement boundary crossed — no funding paid yet
            return 0m;
        }

        // Continuous settlement (Hyperliquid, Lighter): pro-rata per ~60s cycle
        return notional * ratePerHour / 60m;
    }

    private void SignalReadyOnce()
    {
        if (_hasSignaled)
        {
            return;
        }

        _hasSignaled = true;
        _readinessSignal.SignalReady();
        _logger.LogInformation("Funding rate readiness signal fired — BotOrchestrator may proceed");
    }

    /// <summary>
    /// Rounds a UTC time down to the most recent settlement boundary.
    /// Settlement boundaries occur at hours where hour % intervalHours == 0.
    /// E.g. for 8-hour intervals: 00:00, 08:00, 16:00 UTC.
    /// </summary>
    internal static DateTime FloorToSettlement(DateTime utc, int intervalHours)
    {
        var flooredHour = utc.Hour - (utc.Hour % intervalHours);
        return new DateTime(utc.Year, utc.Month, utc.Day, flooredHour, 0, 0, DateTimeKind.Utc);
    }
}
