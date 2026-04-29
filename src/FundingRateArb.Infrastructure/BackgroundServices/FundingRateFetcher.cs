using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
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

    // Exchanges that report per-symbol funding interval via a polling endpoint and
    // therefore participate in the reconciliation pass below. Hyperliquid, Lighter,
    // and dYdX do not expose a fixed interval and are intentionally excluded.
    private static readonly HashSet<string> DetectionCapableExchanges =
        new(StringComparer.OrdinalIgnoreCase) { "Binance", "Aster" };

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
    // One-shot flag: prewarm the SignalEngine opportunity cache after the first successful fetch.
    private bool _hasPrewarmed;

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
            await PrewarmSignalEngineOnceAsync(ct);
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
        var intervalRepo = scope.ServiceProvider.GetService<IAssetExchangeFundingIntervalRepository>();

        var connectors = factory.GetAllConnectors()
            .Where(c => c.MarketType != ExchangeMarketType.Spot)
            .ToList();

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

        // Auto-discover any symbols not yet in the Assets table so the assetMap lookup below
        // finds them. The discovery service invalidates the asset cache on insertion, so the
        // GetActiveAsync call below returns the updated list.
        var discovery = scope.ServiceProvider.GetRequiredService<IAssetDiscoveryService>();
        var discovered = await discovery.EnsureAssetsExistAsync(
            allRates.Select(r => r.Symbol), ct);

        // Resolve exchange and asset lookup tables once.
        // NB11: GetActiveAsync shallow-clones the cached list on every call. On the
        // no-op discovery path (99.9% of cycles) discovery returned 0 and the cache
        // is still valid from the call inside the service — we intentionally let
        // the shared cache absorb the duplication there. When discovery inserted
        // rows, it invalidated the cache, so this call re-hydrates from the DB.
        var exchanges = await uow.Exchanges.GetActiveAsync();
        var assets = await uow.Assets.GetActiveAsync();
        _ = discovered; // keeping the count for future telemetry hooks

        // H-FR2: Build lookup dictionaries before the loop — O(N+M) instead of O(N*M)
        var exchangeMap = exchanges.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var assetMap = assets.ToDictionary(a => a.Symbol, StringComparer.OrdinalIgnoreCase);

        // Detect funding interval changes per detection-capable exchange using modal (most common) value
        var validIntervals = new HashSet<int> { 4, 8 };
        var ratesWithInterval = allRates
            .Where(r => r.DetectedFundingIntervalHours.HasValue
                && DetectionCapableExchanges.Contains(r.ExchangeName))
            .ToList();

        // N3: Early-out when no detection-capable rates carry a DetectedFundingIntervalHours
        if (ratesWithInterval.Count > 0)
        {
            var cachePendingInvalidation = false;
            foreach (var group in ratesWithInterval.GroupBy(r => r.ExchangeName))
            {
                if (!exchangeMap.TryGetValue(group.Key, out var exchangeRow))
                {
                    continue;
                }

                var detectedInterval = group
                    .GroupBy(r => r.DetectedFundingIntervalHours!.Value)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .First();

                if (!validIntervals.Contains(detectedInterval)
                    || detectedInterval == exchangeRow.FundingIntervalHours)
                {
                    continue;
                }

                // NB6: Confirm change against tracked entity to prevent stale-cache no-op updates
                var tracked = await uow.Exchanges.GetByNameAsync(group.Key);
                if (tracked is not null && tracked.FundingIntervalHours != detectedInterval)
                {
                    _logger.LogWarning(
                        "{Exchange} funding interval changed from {Old}h to {New}h — updating exchange entity",
                        group.Key, tracked.FundingIntervalHours, detectedInterval);

                    tracked.FundingIntervalHours = detectedInterval;
                    uow.Exchanges.Update(tracked);
                    cachePendingInvalidation = true;
                }
            }

            if (cachePendingInvalidation)
            {
                await uow.SaveAsync(ct);
                // N2: InvalidateCache after save so consumers see persisted data
                uow.Exchanges.InvalidateCache();
            }
        }

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
                DetectedFundingIntervalHours = rate.DetectedFundingIntervalHours,
            });
        }

        uow.FundingRates.AddRange(snapshots);
        await uow.SaveAsync(ct);

        // Per-symbol funding interval upsert — runs after snapshot save so IDs are assigned
        var perSymbolEntries = allRates
            .Where(r => r.DetectedFundingIntervalHours is { } iv && iv > 0
                        && exchangeMap.ContainsKey(r.ExchangeName)
                        && assetMap.ContainsKey(r.Symbol))
            .Select(r =>
            {
                var exchangeId = exchangeMap[r.ExchangeName].Id;
                var assetId = assetMap[r.Symbol].Id;
                var snapshot = snapshots.FirstOrDefault(s => s.ExchangeId == exchangeId && s.AssetId == assetId);
                return (exchangeId, assetId, r.DetectedFundingIntervalHours!.Value, snapshot?.Id == 0 ? (int?)null : snapshot?.Id);
            })
            .ToList();

        if (intervalRepo is not null && perSymbolEntries.Count > 0)
        {
            await intervalRepo.UpsertManyAsync(perSymbolEntries, ct);
            intervalRepo.InvalidateCache();
        }

        _logger.LogInformation(
            "Funding rates saved: {Total} snapshots from {ExchangeCount} exchanges",
            snapshots.Count, connectors.Count);

        // H-FR1: Hourly purge — keep only 48h of data to prevent unbounded table growth
        if (DateTime.UtcNow - _lastPurgeUtc >= TimeSpan.FromHours(1))
        {
            var cutoff = DateTime.UtcNow.AddHours(-48);
            var purged = await uow.FundingRates.PurgeOlderThanAsync(cutoff, ct: ct);
            _lastPurgeUtc = DateTime.UtcNow;
            _logger.LogInformation("Purged {Count} funding rate snapshots older than {Cutoff:u}", purged, cutoff);
        }

        // Hourly aggregation: at minute >= 5 of each hour, aggregate the previous hour's raw snapshots
        await TryAggregateHourlyAsync(uow, ct);

        // Prune CoinGlass analytics snapshots (>7 days)
        try
        {
            var analyticsRepo = scope.ServiceProvider.GetRequiredService<ICoinGlassAnalyticsRepository>();
            await analyticsRepo.PruneOldSnapshotsAsync(7, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CoinGlass analytics pruning failed");
        }

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

        // Check if aggregates already exist for this hour (prevents duplicate key on container restart)
        var aggregatesExist = await uow.FundingRates.HourlyAggregatesExistAsync(previousHourStart, currentHourStart, ct);
        if (aggregatesExist)
        {
            _logger.LogDebug("Aggregates already exist for hour {Hour:u}, skipping insertion", previousHourStart);
            _lastAggregatedHourUtc = previousHourStart;
            return;
        }

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

            // Hoist exchange lookups once per position
            var longExchange = exchangeById.GetValueOrDefault(pos.LongExchangeId);
            var shortExchange = exchangeById.GetValueOrDefault(pos.ShortExchangeId);

            // Compute notional per-leg using exchange-specific price reference
            var longNotional = ComputeNotional(pos, pos.LongEntryPrice, longRate, longExchange);
            var shortNotional = ComputeNotional(pos, pos.ShortEntryPrice, shortRate, shortExchange);

            // Compute each leg's funding contribution separately to handle mixed settlement types.
            // Convention: short leg earns funding (positive), long leg pays funding (negative).
            var longFunding = ComputeLegFunding(
                longRate.RatePerHour, longNotional, longExchange, now);
            var shortFunding = ComputeLegFunding(
                shortRate.RatePerHour, shortNotional, shortExchange, now);

            // Apply exchange rebate on the paying side
            // Long leg pays when rate > 0; short leg pays when rate < 0
            longFunding = ApplyRebate(longFunding, longExchange, isPaying: longRate.RatePerHour > 0);
            shortFunding = ApplyRebate(shortFunding, shortExchange, isPaying: shortRate.RatePerHour < 0);

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
        Exchange? exchange,
        DateTime now)
    {
        if (exchange is null)
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

            var lastCycle = _lastCycleTimePerExchange.GetValueOrDefault(exchange.Id, DateTime.MinValue);

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
                    exchange.Id, intervalFunding, ratePerHour, intervalHours);
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
    /// Invokes <see cref="ISignalEngine.EvaluateAllAsync"/> once per process lifetime to prime
    /// the opportunity cache immediately after the first successful funding-rate fetch.
    /// The prewarm ensures that the first dashboard request finds leverage-tier metrics already
    /// populated, eliminating the cold-start latency on initial page load.
    /// Any exception is caught and logged as a warning — the prewarm must never abort startup.
    /// </summary>
    private async Task PrewarmSignalEngineOnceAsync(CancellationToken ct)
    {
        if (_hasPrewarmed)
        {
            return;
        }

        _hasPrewarmed = true;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            await signalEngine.EvaluateAllAsync(ct);
            _logger.LogDebug("SignalEngine opportunity cache pre-warmed after first funding-rate fetch");
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown during prewarm — expected, not a fault.
            _logger.LogDebug("SignalEngine prewarm cancelled during shutdown; continuing");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalEngine prewarm failed; continuing startup");
        }
    }

    /// <summary>
    /// Computes the notional value for a position leg using the exchange's configured price reference.
    /// Oracle-price exchanges (e.g. Hyperliquid, dYdX) use IndexPrice; mark-price exchanges use MarkPrice.
    /// Falls back to SizeUsdc * Leverage when entry price or snapshot price is unavailable.
    /// </summary>
    internal static decimal ComputeNotional(ArbitragePosition pos, decimal entryPrice, FundingRateSnapshot rate, Exchange? exchange)
    {
        var fallbackNotional = pos.SizeUsdc * pos.Leverage;
        if (entryPrice <= 0 || exchange is null)
        {
            return fallbackNotional;
        }

        var quantity = fallbackNotional / entryPrice;
        var priceRef = exchange.FundingNotionalPriceType == FundingNotionalPriceType.OraclePrice
            ? rate.IndexPrice
            : rate.MarkPrice;

        return priceRef > 0 ? quantity * priceRef : fallbackNotional;
    }

    /// <summary>
    /// Applies the exchange's funding rebate to a funding amount.
    /// Rebate only applies when the leg is paying funding (determined by rate direction).
    /// Moves the funding amount toward zero by the rebate fraction.
    /// </summary>
    internal decimal ApplyRebate(decimal funding, Exchange? exchange, bool isPaying)
    {
        if (!isPaying || exchange is null || exchange.FundingRebateRate <= 0)
        {
            return funding;
        }

        var effectiveRate = Math.Clamp(exchange.FundingRebateRate, 0m, 1m);
        var rebateAmount = Math.Abs(funding) * effectiveRate;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Funding rebate applied for {Exchange}: raw={Raw:F6}, rebate={Rebate:F6} ({Rate:P0})",
                exchange.Name, funding, rebateAmount, effectiveRate);
        }

        return funding > 0 ? funding - rebateAmount : funding + rebateAmount;
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
