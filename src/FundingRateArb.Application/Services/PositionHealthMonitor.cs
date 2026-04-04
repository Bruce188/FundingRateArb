using System.Collections.Concurrent;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class PositionHealthMonitor : IPositionHealthMonitor
{
    private readonly IUnitOfWork _uow;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IMarketDataCache _marketDataCache;
    private readonly IReferencePriceProvider _referencePriceProvider;
    private readonly IExecutionEngine _executionEngine;
    private readonly ILeverageTierProvider _tierProvider;
    private readonly ILogger<PositionHealthMonitor> _logger;
    private readonly ConcurrentDictionary<int, int> _priceFetchFailures = new();
    private readonly ConcurrentDictionary<int, int> _zeroPriceCheckCounts = new();
    private readonly ConcurrentDictionary<int, DateTime> _recentMarginAlerts = new();
    private readonly ConcurrentDictionary<int, int> _negativeFundingCycles = new();
    private int _stablecoinCheckCycle = -1;

    public PositionHealthMonitor(
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        IMarketDataCache marketDataCache,
        IReferencePriceProvider referencePriceProvider,
        IExecutionEngine executionEngine,
        ILeverageTierProvider tierProvider,
        ILogger<PositionHealthMonitor> logger)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
        _marketDataCache = marketDataCache;
        _referencePriceProvider = referencePriceProvider;
        _executionEngine = executionEngine;
        _tierProvider = tierProvider;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckAndActAsync(CancellationToken ct = default)
    {
        var allReaped = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>();

        // M4: Reap stale Opening positions (stuck > 5 minutes)
        var openingReaped = await ReapStalePositionsAsync(PositionStatus.Opening, TimeSpan.FromMinutes(5), ct);
        allReaped.AddRange(openingReaped);
        // Fetch Closing positions once — shared between reaper and retry logic (NB11)
        var closingPositions = await _uow.Positions.GetByStatusAsync(PositionStatus.Closing);
        // M4: Reap stale Closing positions (stuck > 10 minutes), returns non-reaped positions
        var (nonReaped, closingReaped) = await ReapStaleClosingPositionsAsync(closingPositions, TimeSpan.FromMinutes(10), ct);
        allReaped.AddRange(closingReaped);
        // Retry close for remaining Closing positions (partial fills)
        await RetryClosingPositionsAsync(nonReaped, ct);

        // C-PR1: Use tracked query so mutations (CurrentSpreadPerHour) are persisted by EF
        var openPositions = await _uow.Positions.GetOpenTrackedAsync();
        if (openPositions.Count == 0)
        {
            // Clean up all tracking dictionaries when no positions are open
            _negativeFundingCycles.Clear();
            _priceFetchFailures.Clear();
            _zeroPriceCheckCounts.Clear();

            return new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                allReaped,
                new Dictionary<int, ComputedPositionPnl>());
        }

        var config = await _uow.BotConfig.GetActiveAsync();

        // Stablecoin depeg check — before position loop (every 5th cycle; USDCUSDT is slow-moving)
        var stablecoinCritical = false;
        if (unchecked((uint)Interlocked.Increment(ref _stablecoinCheckCycle)) % 5 == 0)
        {
            stablecoinCritical = await CheckStablecoinDepegAsync(config, ct);
        }

        var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();

        // H3: Build a dictionary for O(1) lookup instead of O(N*M) linear scan
        var rateMap = latestRates.ToDictionary(r => (r.ExchangeId, r.AssetId));

        // NB3: Exchange name lookup for null navigation property fallback (cross-stablecoin check)
        var exchangeNameById = latestRates
            .Where(r => r.Exchange?.Name is not null)
            .GroupBy(r => r.ExchangeId)
            .ToDictionary(g => g.Key, g => g.First().Exchange!.Name);

        // Pre-fetch recent alerts for all open positions to avoid N+1 queries in the loop
        var openPositionIds = openPositions.Select(p => p.Id).ToList();
        var recentAlerts = await _uow.Alerts.GetRecentByPositionIdsAsync(
            openPositionIds,
            new[] { AlertType.MarginWarning, AlertType.SpreadWarning },
            TimeSpan.FromHours(4));

        // C-PH1: Collect positions that need closing; call SaveAsync ONCE after the loop
        var toClose = new List<(ArbitragePosition Position, CloseReason Reason)>();
        var computedPnl = new Dictionary<int, ComputedPositionPnl>();

        foreach (var pos in openPositions)
        {
            var assetSymbol = pos.Asset?.Symbol ?? "?";
            var longExchangeName = pos.LongExchange?.Name ?? "?";
            var shortExchangeName = pos.ShortExchange?.Name ?? "?";

            // Compute current spread from latest funding rate snapshots (O(1) dictionary lookup)
            rateMap.TryGetValue((pos.LongExchangeId, pos.AssetId), out var longRate);
            rateMap.TryGetValue((pos.ShortExchangeId, pos.AssetId), out var shortRate);

            if (longRate is not null && shortRate is not null)
            {
                var computedSpread = shortRate.RatePerHour - longRate.RatePerHour;
                pos.CurrentSpreadPerHour = computedSpread;
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Missing funding rate for position #{Id}: longRate={LongAvailable}, shortRate={ShortAvailable} — keeping previous spread",
                        pos.Id, longRate is not null, shortRate is not null);
                }
            }

            // Use whatever spread is current (updated above or previous value)
            var spread = pos.CurrentSpreadPerHour;

            // Track consecutive negative funding cycles for FundingFlipped close
            if (spread < 0)
            {
                _negativeFundingCycles.AddOrUpdate(pos.Id, 1, (_, c) => c + 1);
            }
            else
            {
                _negativeFundingCycles.TryRemove(pos.Id, out _);
            }

            // Stablecoin depeg: close cross-stablecoin positions when critical
            if (stablecoinCritical)
            {
                var longExName = pos.LongExchange?.Name ?? exchangeNameById.GetValueOrDefault(pos.LongExchangeId);
                var shortExName = pos.ShortExchange?.Name ?? exchangeNameById.GetValueOrDefault(pos.ShortExchangeId);
                var isCrossStablecoin = (longExName == "Binance") != (shortExName == "Binance");
                if (isCrossStablecoin)
                {
                    _logger.LogWarning("Closing cross-stablecoin position #{Id}: stablecoin depeg critical", pos.Id);
                    toClose.Add((pos, CloseReason.StablecoinDepeg));
                    continue;
                }
            }

            if (pos.LongEntryPrice <= 0 || pos.ShortEntryPrice <= 0)
            {
                var checkCount = _zeroPriceCheckCounts.AddOrUpdate(pos.Id, 1, (_, c) => c + 1);
                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    _logger.LogCritical(
                        "Position #{Id} has zero entry prices — check {Count}/3, stop-loss disabled",
                        pos.Id, checkCount);
                }

                if (checkCount >= 3)
                {
                    if (_logger.IsEnabled(LogLevel.Critical))
                    {
                        _logger.LogCritical(
                            "Position #{Id} zero entry prices persisted for {Count} checks — force-closing",
                            pos.Id, checkCount);
                    }
                    toClose.Add((pos, CloseReason.StopLoss));
                    // NB6: Reset to 0 instead of removing — if close fails, next cycle restarts at 1
                    _zeroPriceCheckCounts[pos.Id] = 0;
                }
                continue;
            }
            // Clean up tracking for positions that now have valid prices
            _zeroPriceCheckCounts.TryRemove(pos.Id, out _);

            try
            {
                // Try WebSocket cache first (sub-second freshness), fall back to REST
                var currentLongMark = _marketDataCache.GetMarkPrice(longExchangeName, assetSymbol);
                var currentShortMark = _marketDataCache.GetMarkPrice(shortExchangeName, assetSymbol);

                if (currentLongMark <= 0 || currentShortMark <= 0)
                {
                    var longConnector = _connectorFactory.GetConnector(longExchangeName);
                    var shortConnector = _connectorFactory.GetConnector(shortExchangeName);
                    var longTask = currentLongMark <= 0 ? longConnector.GetMarkPriceAsync(assetSymbol, ct) : Task.FromResult(currentLongMark);
                    var shortTask = currentShortMark <= 0 ? shortConnector.GetMarkPriceAsync(assetSymbol, ct) : Task.FromResult(currentShortMark);
                    await Task.WhenAll(longTask, shortTask);
                    currentLongMark = await longTask;
                    currentShortMark = await shortTask;
                }

                // M5: Compute net unrealized PnL for stop-loss check
                var avgEntryPrice = (pos.LongEntryPrice + pos.ShortEntryPrice) / 2m;
                var estimatedQty = avgEntryPrice > 0
                    ? pos.SizeUsdc * pos.Leverage / avgEntryPrice
                    : 0m;
                var longPnl = (currentLongMark - pos.LongEntryPrice) * estimatedQty;
                var shortPnl = (pos.ShortEntryPrice - currentShortMark) * estimatedQty;
                var unrealizedPnl = longPnl + shortPnl;

                // Collateral imbalance monitoring
                var marginPerLeg = pos.MarginUsdc / 2m;
                decimal? collateralImbalancePct = null;
                if (marginPerLeg > 0)
                {
                    var longUtil = Math.Abs(longPnl) / marginPerLeg;
                    var shortUtil = Math.Abs(shortPnl) / marginPerLeg;
                    collateralImbalancePct = Math.Abs(longUtil - shortUtil);

                    if (collateralImbalancePct > 0.30m)
                    {
                        var winningExchange = longPnl > shortPnl ? longExchangeName : shortExchangeName;
                        var losingExchange = longPnl > shortPnl ? shortExchangeName : longExchangeName;

                        var hasRecentImbalanceAlert = recentAlerts.ContainsKey((pos.Id, AlertType.MarginWarning));
                        if (!hasRecentImbalanceAlert)
                        {
                            _uow.Alerts.Add(new Alert
                            {
                                UserId = pos.UserId,
                                ArbitragePositionId = pos.Id,
                                Type = AlertType.MarginWarning,
                                Severity = AlertSeverity.Warning,
                                Message = $"Collateral imbalance: {assetSymbol} {longExchangeName}/{shortExchangeName} " +
                                          $"imbalance={collateralImbalancePct:P0}. Consider rebalancing from {winningExchange} to {losingExchange}.",
                            });
                        }
                    }
                }

                // Unified PnL: both legs valued against single reference price
                var unifiedPrice = _referencePriceProvider.GetUnifiedPrice(assetSymbol, longExchangeName, shortExchangeName);
                decimal unifiedUnrealizedPnl;
                if (unifiedPrice > 0)
                {
                    var unifiedLongPnl = (unifiedPrice - pos.LongEntryPrice) * estimatedQty;
                    var unifiedShortPnl = (pos.ShortEntryPrice - unifiedPrice) * estimatedQty;
                    unifiedUnrealizedPnl = unifiedLongPnl + unifiedShortPnl;
                }
                else
                {
                    // Unified price unavailable (data feed gap) — fall back to per-exchange PnL
                    _logger.LogWarning(
                        "Unified price unavailable for {Asset} ({Long}/{Short}), falling back to per-exchange PnL",
                        assetSymbol, longExchangeName, shortExchangeName);
                    unifiedUnrealizedPnl = unrealizedPnl;
                }

                // Price divergence tracking
                if (unifiedPrice > 0)
                {
                    var newDivergencePct = Math.Abs(currentLongMark - currentShortMark) / unifiedPrice * 100m;
                    if (pos.CurrentDivergencePct is null ||
                        Math.Abs(pos.CurrentDivergencePct.Value - newDivergencePct) >= 0.0001m)
                    {
                        pos.CurrentDivergencePct = newDivergencePct;
                    }

                    // Alert if divergence exceeds threshold
                    var entryMid = (pos.LongEntryPrice + pos.ShortEntryPrice) / 2m;
                    var entrySpreadCostPct = entryMid > 0
                        ? Math.Abs(pos.ShortEntryPrice - pos.LongEntryPrice) / entryMid * 100m
                        : 0m;
                    var divergenceThreshold = config.DivergenceAlertMultiplier * entrySpreadCostPct;

                    if (pos.CurrentDivergencePct > divergenceThreshold && divergenceThreshold > 0)
                    {
                        var hasRecentDivAlert = recentAlerts.ContainsKey((pos.Id, AlertType.SpreadWarning));
                        if (!hasRecentDivAlert)
                        {
                            _uow.Alerts.Add(new Alert
                            {
                                UserId = pos.UserId,
                                ArbitragePositionId = pos.Id,
                                Type = AlertType.SpreadWarning,
                                Severity = AlertSeverity.Warning,
                                Message = $"Price divergence warning: {assetSymbol} " +
                                          $"{longExchangeName}/{shortExchangeName} " +
                                          $"divergence={pos.CurrentDivergencePct:F2}% (threshold={divergenceThreshold:F2}%)",
                            });
                        }
                    }
                }

                // Track computed PnL for downstream DTO population
                computedPnl[pos.Id] = new ComputedPositionPnl(
                    ExchangePnl: unrealizedPnl,
                    UnifiedPnl: unifiedUnrealizedPnl,
                    DivergencePct: pos.CurrentDivergencePct ?? 0m,
                    CollateralImbalancePct: collateralImbalancePct);

                // Calculate liquidation prices and distance
                var minLiquidationDistance = ComputeLiquidationDistance(
                    pos, currentLongMark, currentShortMark);

                var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;

                // Determine close reason (priority: stop-loss > liquidation > PnL target > max hold > spread collapsed)
                // Intentional: all close reasons including PnlTargetReached use unified PnL (strategy-level profit
                // target, not per-exchange margin impact). This ensures consistent close logic against a single
                // reference price, avoiding false triggers from cross-exchange price divergence.
                _negativeFundingCycles.TryGetValue(pos.Id, out var flipCount);
                var reason = DetermineCloseReason(pos, config, unifiedUnrealizedPnl, hoursOpen, spread, minLiquidationDistance, flipCount);

                if (reason.HasValue)
                {
                    _logger.LogWarning(
                        "Auto-closing position #{PositionId}: {Asset} " +
                        "reason={CloseReason}, spread={Spread}/hour, " +
                        "hoursOpen={HoursOpen:F1}, unrealizedPnl={UnrealizedPnl:F2}, unifiedPnl={UnifiedPnl:F2}",
                        pos.Id, assetSymbol, reason.Value,
                        spread, hoursOpen, unrealizedPnl, unifiedUnrealizedPnl);

                    toClose.Add((pos, reason.Value));
                    continue; // skip alert check — position will be closed
                }

                // Liquidation early warning at 2x threshold
                if (minLiquidationDistance.HasValue && minLiquidationDistance.Value < config.LiquidationWarningPct * 2m)
                {
                    var hasRecentLiqAlert = recentAlerts.ContainsKey((pos.Id, AlertType.MarginWarning));

                    if (!hasRecentLiqAlert)
                    {
                        _uow.Alerts.Add(new Alert
                        {
                            UserId = pos.UserId,
                            ArbitragePositionId = pos.Id,
                            Type = AlertType.MarginWarning,
                            Severity = AlertSeverity.Warning,
                            Message = $"Liquidation warning: {assetSymbol} " +
                                      $"{longExchangeName}/{shortExchangeName} " +
                                      $"distance={minLiquidationDistance.Value:P1} (threshold={config.LiquidationWarningPct:P1})",
                        });
                    }
                }

                // Margin utilization monitoring via exchange API
                await CheckMarginUtilizationAsync(pos, config, longExchangeName, shortExchangeName, assetSymbol, ct);

                // Alert if spread below alert threshold (but above close threshold)
                if (spread < config.AlertThreshold)
                {
                    var hasRecentSpreadAlert = recentAlerts.ContainsKey((pos.Id, AlertType.SpreadWarning));

                    if (!hasRecentSpreadAlert)
                    {
                        _uow.Alerts.Add(new Alert
                        {
                            UserId = pos.UserId,
                            ArbitragePositionId = pos.Id,
                            Type = AlertType.SpreadWarning,
                            Severity = AlertSeverity.Warning,
                            Message = $"Spread warning: {assetSymbol} " +
                                                  $"{longExchangeName}/{shortExchangeName} " +
                                                  $"spread={spread:F6}/hour (threshold={config.AlertThreshold:F6})",
                        });
                    }
                }

                _priceFetchFailures.TryRemove(pos.Id, out _);
            }
            catch (Exception ex)
            {
                var failures = _priceFetchFailures.AddOrUpdate(pos.Id, 1, (_, v) => v + 1);
                _logger.LogWarning(ex, "Failed to check health for position #{Id} ({Failures} consecutive): {Message}",
                    pos.Id, failures, ex.Message);

                if (failures >= config.PriceFeedFailureCloseThreshold)
                {
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = pos.UserId,
                        ArbitragePositionId = pos.Id,
                        Type = AlertType.PriceFeedFailure,
                        Severity = AlertSeverity.Critical,
                        Message = $"Price feed failed {failures} consecutive times for " +
                                  $"{pos.Asset?.Symbol ?? "unknown"}. " +
                                  "Force-closing position — stop-loss protection inactive.",
                    });
                    toClose.Add((pos, CloseReason.PriceFeedLost));
                }
            }
        }

        // Clean up stale tracking entries for positions no longer open
        var openIds = new HashSet<int>(openPositions.Select(p => p.Id));
        foreach (var key in _negativeFundingCycles.Keys)
            if (!openIds.Contains(key))
                _negativeFundingCycles.TryRemove(key, out _);

        // C-PH1: Single SaveAsync call after the loop — persists all spread updates and new alerts
        await _uow.SaveAsync(ct);

        return new HealthCheckResult(toClose, allReaped, computedPnl);
    }

    private async Task RetryClosingPositionsAsync(IReadOnlyList<ArbitragePosition> positions, CancellationToken ct)
    {
        if (positions.Count == 0)
        {
            return;
        }

        // NB4: Cap per-cycle retry count to bound total wall time (N x 45s)
        const int maxRetriesPerCycle = 6;

        foreach (var pos in positions.OrderBy(p => p.ClosingStartedAt ?? p.OpenedAt).Take(maxRetriesPerCycle))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(45));
                var closeReason = pos.CloseReason ?? CloseReason.SpreadCollapsed;
                if (!pos.CloseReason.HasValue)
                {
                    _logger.LogWarning("Position #{PositionId} has no CloseReason set, defaulting to {DefaultReason}", pos.Id, closeReason);
                }
                _logger.LogInformation("Retrying close for position #{PositionId} stuck in Closing status", pos.Id);
                await _executionEngine.ClosePositionAsync(pos.UserId, pos, closeReason, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Retry close timed out for position #{PositionId}", pos.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retry close failed for position #{PositionId}: {Message}", pos.Id, ex.Message);
            }
        }
    }

    private async Task<(IReadOnlyList<ArbitragePosition> NonReaped, List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)> Reaped)> ReapStaleClosingPositionsAsync(
        IReadOnlyList<ArbitragePosition> closingPositions, TimeSpan maxAge, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var nonReaped = new List<ArbitragePosition>();
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>();

        foreach (var pos in closingPositions)
        {
            var referenceTime = pos.ClosingStartedAt ?? pos.OpenedAt;
            if (referenceTime >= cutoff)
            {
                nonReaped.Add(pos);
                continue;
            }

            if (_logger.IsEnabled(LogLevel.Critical))
            {
                _logger.LogCritical(
                    "Reaping stale {Status} position #{PositionId} ({Asset}) — stuck since {OpenedAt}",
                    PositionStatus.Closing, pos.Id, pos.Asset?.Symbol ?? "?", referenceTime);
            }

            pos.Status = PositionStatus.EmergencyClosed;
            pos.ClosedAt = DateTime.UtcNow;
            _uow.Positions.Update(pos);
            _uow.Alerts.Add(new Alert
            {
                UserId = pos.UserId,
                ArbitragePositionId = pos.Id,
                Type = AlertType.LegFailed,
                Severity = AlertSeverity.Critical,
                Message = $"Position #{pos.Id} stuck in Closing for >{maxAge.TotalMinutes:F0} minutes. " +
                           $"Auto-transitioned to EmergencyClosed. Manual intervention required.",
            });
            reapedPositions.Add((pos.Id, pos.UserId, pos.LongExchangeId, pos.ShortExchangeId, PositionStatus.Closing));
        }

        if (reapedPositions.Count > 0)
        {
            await _uow.SaveAsync(ct);
        }

        return (nonReaped, reapedPositions);
    }

    private async Task<List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>> ReapStalePositionsAsync(PositionStatus status, TimeSpan maxAge, CancellationToken ct)
    {
        var positions = await _uow.Positions.GetByStatusAsync(status);
        var cutoff = DateTime.UtcNow - maxAge;
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>();

        foreach (var pos in positions)
        {
            // For Closing status, use ClosingStartedAt; for Opening, use OpenedAt
            var referenceTime = status == PositionStatus.Closing
                ? pos.ClosingStartedAt ?? pos.OpenedAt
                : pos.OpenedAt;

            if (referenceTime >= cutoff)
            {
                continue;
            }

            if (_logger.IsEnabled(LogLevel.Critical))
            {
                _logger.LogCritical(
                    "Reaping stale {Status} position #{PositionId} ({Asset}) — stuck since {OpenedAt}",
                    status, pos.Id, pos.Asset?.Symbol ?? "?", referenceTime);
            }

            pos.Status = PositionStatus.EmergencyClosed;
            pos.ClosedAt = DateTime.UtcNow;
            _uow.Positions.Update(pos);
            _uow.Alerts.Add(new Alert
            {
                UserId = pos.UserId,
                ArbitragePositionId = pos.Id,
                Type = AlertType.LegFailed,
                Severity = AlertSeverity.Critical,
                Message = $"Position #{pos.Id} stuck in {status} for >{maxAge.TotalMinutes:F0} minutes. " +
                           $"Auto-transitioned to EmergencyClosed. Manual intervention required.",
            });
            reapedPositions.Add((pos.Id, pos.UserId, pos.LongExchangeId, pos.ShortExchangeId, status));
        }

        if (reapedPositions.Count > 0)
        {
            await _uow.SaveAsync(ct);
        }

        return reapedPositions;
    }

    public static CloseReason? DetermineCloseReason(
        ArbitragePosition pos, BotConfiguration config,
        decimal unrealizedPnl, decimal hoursOpen, decimal spread,
        decimal? minLiquidationDistance = null,
        int negativeFundingCycles = 0)
    {
        // Priority: StopLoss > LiquidationRisk > PnlTargetReached > EmergencySpread > MaxHoldTime > SpreadCollapsed
        if (pos.MarginUsdc > 0 && unrealizedPnl < 0 && Math.Abs(unrealizedPnl) >= config.StopLossPct * pos.MarginUsdc)
        {
            return CloseReason.StopLoss;
        }

        if (minLiquidationDistance.HasValue && minLiquidationDistance.Value < config.LiquidationWarningPct)
        {
            return CloseReason.LiquidationRisk;
        }

        if (config.AdaptiveHoldEnabled && pos.AccumulatedFunding > 0)
        {
            var entryFee = pos.EntryFeesUsdc > 0
                ? pos.EntryFeesUsdc
                : pos.LongExchange is not null && pos.ShortExchange is not null
                    ? pos.SizeUsdc * pos.Leverage * 2m * GetTakerFeeRate(
                        pos.LongExchange.Name, pos.ShortExchange.Name,
                        pos.LongExchange.TakerFeeRate, pos.ShortExchange.TakerFeeRate)
                    : 0m;
            if (entryFee > 0 && pos.AccumulatedFunding >= config.TargetPnlMultiplier * entryFee)
            {
                // Total PnL must be positive (unrealized + accumulated - entry fees)
                var totalPnl = unrealizedPnl + pos.AccumulatedFunding - entryFee;
                // Minimum hold time before PnlTargetReached can fire
                var minutesOpen = hoursOpen * 60m;
                if (totalPnl > 0 && minutesOpen >= config.MinHoldBeforePnlTargetMinutes)
                {
                    return CloseReason.PnlTargetReached;
                }
            }
        }

        // Emergency spread: bypass MinHoldTimeHours for catastrophic spread reversal
        if (spread < config.EmergencyCloseSpreadThreshold)
        {
            return CloseReason.SpreadCollapsed;
        }

        // Funding flipped: spread has been negative for too many consecutive cycles
        if (negativeFundingCycles >= config.FundingFlipExitCycles && config.FundingFlipExitCycles > 0)
        {
            return CloseReason.FundingFlipped;
        }

        if (hoursOpen >= config.MaxHoldTimeHours)
        {
            return CloseReason.MaxHoldTimeReached;
        }

        if (spread < config.CloseThreshold && hoursOpen >= config.MinHoldTimeHours)
        {
            return CloseReason.SpreadCollapsed;
        }

        return null;
    }

    /// <summary>
    /// Calculates liquidation prices for both legs and returns the minimum distance
    /// as a fraction (0.0 = at liquidation, 1.0 = 100% distance from liquidation).
    /// Updates LongLiquidationPrice and ShortLiquidationPrice on the position.
    /// Returns null if leverage is zero or entry prices are invalid.
    /// </summary>
    public static decimal? ComputeLiquidationDistance(
        ArbitragePosition pos, decimal currentLongMark, decimal currentShortMark)
    {
        if (pos.Leverage <= 0 || pos.LongEntryPrice <= 0 || pos.ShortEntryPrice <= 0)
        {
            return null;
        }

        // Long liquidation: price drops to entry * (1 - 1/leverage)
        var longLiqPrice = pos.LongEntryPrice * (1m - 1m / pos.Leverage);
        // Short liquidation: price rises to entry * (1 + 1/leverage)
        var shortLiqPrice = pos.ShortEntryPrice * (1m + 1m / pos.Leverage);

        pos.LongLiquidationPrice = longLiqPrice;
        pos.ShortLiquidationPrice = shortLiqPrice;

        // Distance from current mark to liquidation as fraction of entry-to-liquidation range
        var longRange = pos.LongEntryPrice - longLiqPrice;
        var shortRange = shortLiqPrice - pos.ShortEntryPrice;

        var longDistance = longRange > 0
            ? (currentLongMark - longLiqPrice) / longRange
            : decimal.MaxValue;
        var shortDistance = shortRange > 0
            ? (shortLiqPrice - currentShortMark) / shortRange
            : decimal.MaxValue;

        return Math.Min(longDistance, shortDistance);
    }

    /// <summary>
    /// Returns the combined taker fee rate from both exchanges (sum, not max).
    /// Each exchange fee is per-trade; the sum represents the fee for one leg pair.
    /// Callers use <c>SizeUsdc * Leverage * 2 * GetTakerFeeRate()</c> for the round-trip fee
    /// of one position (open + close = 4 trades across 2 exchanges).
    /// When DB-stored fee rates are available (via Exchange.TakerFeeRate), pass them
    /// to avoid reliance on the hardcoded fallback table.
    /// </summary>
    public static decimal GetTakerFeeRate(
        string? longExchange, string? shortExchange,
        decimal? longTakerFeeRate = null, decimal? shortTakerFeeRate = null)
    {
        return GetExchangeTakerFee(longExchange, longTakerFeeRate)
             + GetExchangeTakerFee(shortExchange, shortTakerFeeRate);
    }

    private async Task CheckMarginUtilizationAsync(
        ArbitragePosition pos, BotConfiguration config,
        string longExchangeName, string shortExchangeName, string assetSymbol,
        CancellationToken ct = default)
    {
        try
        {
            var longConnector = _connectorFactory.GetConnector(longExchangeName);
            var shortConnector = _connectorFactory.GetConnector(shortExchangeName);

            // Deduplicate: if both legs use the same connector for the same asset, call once
            Task<MarginStateDto?> longMarginTask;
            Task<MarginStateDto?> shortMarginTask;
            if (string.Equals(longExchangeName, shortExchangeName, StringComparison.OrdinalIgnoreCase))
            {
                longMarginTask = longConnector.GetPositionMarginStateAsync(assetSymbol, ct);
                shortMarginTask = longMarginTask;
            }
            else
            {
                longMarginTask = longConnector.GetPositionMarginStateAsync(assetSymbol, ct);
                shortMarginTask = shortConnector.GetPositionMarginStateAsync(assetSymbol, ct);
            }

            await Task.WhenAll(longMarginTask, shortMarginTask);

            var longMargin = await longMarginTask;
            var shortMargin = await shortMarginTask;

            // Check if either leg exceeds the margin utilization alert threshold
            var maxUtilization = Math.Max(
                longMargin?.MarginUtilizationPct ?? 0m,
                shortMargin?.MarginUtilizationPct ?? 0m);

            if (maxUtilization >= config.MarginUtilizationAlertPct)
            {
                // NB12: In-memory dedup before hitting the DB
                if (_recentMarginAlerts.TryGetValue(pos.Id, out var lastAlertTime)
                    && DateTime.UtcNow - lastAlertTime < TimeSpan.FromHours(1))
                {
                    return;
                }

                var recentMarginAlert = await _uow.Alerts.GetRecentAsync(
                    pos.UserId, pos.Id, AlertType.MarginWarning, TimeSpan.FromHours(1));

                if (recentMarginAlert is null)
                {
                    var longPct = longMargin?.MarginUtilizationPct ?? 0m;
                    var shortPct = shortMargin?.MarginUtilizationPct ?? 0m;
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = pos.UserId,
                        ArbitragePositionId = pos.Id,
                        Type = AlertType.MarginWarning,
                        Severity = AlertSeverity.Warning,
                        Message = $"Margin utilization alert: {assetSymbol} " +
                                  $"{longExchangeName}={longPct:P0} / {shortExchangeName}={shortPct:P0} " +
                                  $"(threshold={config.MarginUtilizationAlertPct:P0})",
                    });
                }

                _recentMarginAlerts[pos.Id] = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Margin utilization check failed for position #{Id}", pos.Id);
        }
    }

    private static decimal GetExchangeTakerFee(string? exchangeName, decimal? dbFeeRate = null)
    {
        // Prefer DB-stored fee rate when available
        if (dbFeeRate.HasValue)
        {
            return dbFeeRate.Value;
        }

        // Fallback to shared constants when DB rate is not loaded
        return ExchangeFeeConstants.GetTakerFeeRate(exchangeName ?? string.Empty);
    }

    private async Task<bool> CheckStablecoinDepegAsync(
        BotConfiguration config, CancellationToken ct)
    {
        try
        {
            var binance = _connectorFactory.GetConnector("Binance");
            var price = await binance.GetMarkPriceAsync("USDCUSDT", ct);
            if (price <= 0) return false;

            var spreadPct = Math.Abs(1.0m - price) * 100m;
            _logger.LogDebug("Stablecoin USDCUSDT spread: {SpreadPct:F4}%", spreadPct);

            if (spreadPct >= config.StablecoinCriticalThresholdPct)
            {
                _logger.LogCritical("Stablecoin depeg CRITICAL: USDCUSDT spread {SpreadPct:F4}% >= {Threshold}%",
                    spreadPct, config.StablecoinCriticalThresholdPct);
                return true;
            }

            if (spreadPct >= config.StablecoinAlertThresholdPct)
            {
                _logger.LogWarning("Stablecoin depeg WARNING: USDCUSDT spread {SpreadPct:F4}% >= {Threshold}%",
                    spreadPct, config.StablecoinAlertThresholdPct);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check stablecoin depeg — skipping");
            return false;
        }
    }

    public async Task ReconcileOpenPositionsAsync(CancellationToken ct = default)
    {
        // Part B.1: Reconcile Opening positions — recover or fail stuck opens
        var openingPositions = await _uow.Positions.GetByStatusAsync(PositionStatus.Opening);
        foreach (var pos in openingPositions)
        {
            try
            {
                var exists = await _executionEngine.CheckPositionExistsOnExchangesAsync(pos, ct);
                if (exists == true)
                {
                    pos.Status = PositionStatus.Open;
                    _uow.Positions.Update(pos);
                    await _uow.SaveAsync(ct);
                    _logger.LogWarning(
                        "Opening position #{Id} ({Asset}) recovered — both legs confirmed on exchanges, transitioned to Open",
                        pos.Id, pos.Asset?.Symbol ?? "?");
                }
                else if (exists == false)
                {
                    // Attempt cleanup of any surviving leg before marking Failed
                    // (if one leg was placed but the other failed, this closes the surviving one)
                    try
                    {
                        await _executionEngine.ClosePositionAsync(pos.UserId, pos, CloseReason.ExchangeDrift, ct);
                    }
                    catch (Exception closeEx)
                    {
                        _logger.LogWarning(closeEx, "Cleanup close failed for Opening position #{Id}", pos.Id);
                    }

                    pos.Status = PositionStatus.Failed;
                    pos.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(pos);
                    await _uow.SaveAsync(ct);
                    _logger.LogWarning(
                        "Opening position #{Id} ({Asset}) not found on exchanges — marked as Failed",
                        pos.Id, pos.Asset?.Symbol ?? "?");
                }
                // null → API failure, skip
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Opening reconciliation failed for position #{Id}", pos.Id);
            }
        }

        // Part B.2: Reconcile Closing positions — finalize if both legs gone
        var closingPositions = await _uow.Positions.GetByStatusAsync(PositionStatus.Closing);
        foreach (var pos in closingPositions)
        {
            try
            {
                var exists = await _executionEngine.CheckPositionExistsOnExchangesAsync(pos, ct);
                if (exists == false)
                {
                    pos.Status = PositionStatus.Closed;
                    pos.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(pos);
                    await _uow.SaveAsync(ct);
                    _logger.LogWarning(
                        "Closing position #{Id} ({Asset}) confirmed gone from exchanges — finalized as Closed",
                        pos.Id, pos.Asset?.Symbol ?? "?");
                }
                // true or one-leg present → leave in Closing (retry logic will handle)
                // null → API failure, skip
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Closing reconciliation failed for position #{Id}", pos.Id);
            }
        }

        // Part A: Reconcile Open positions — detect exchange drift
        var openPositions = await _uow.Positions.GetOpenTrackedAsync();
        if (openPositions.Count == 0)
        {
            return;
        }

        var batchResults = await _executionEngine.CheckPositionsExistOnExchangesBatchAsync(openPositions, ct);

        var reconciled = 0;
        var survivingLegCloses = new List<ArbitragePosition>();
        foreach (var pos in openPositions)
        {
            try
            {
                if (!batchResults.TryGetValue(pos.Id, out var result))
                {
                    _logger.LogDebug("Position #{Id} not included in batch results — skipping", pos.Id);
                    continue;
                }

                if (result == PositionExistsResult.Unknown || result == PositionExistsResult.BothPresent)
                {
                    continue;
                }

                string driftDetail;
                switch (result)
                {
                    case PositionExistsResult.BothMissing:
                        driftDetail = "both legs missing from exchanges";
                        break;
                    case PositionExistsResult.LongMissing:
                        driftDetail = $"long leg missing from {pos.LongExchange?.Name}, short leg still present on {pos.ShortExchange?.Name}";
                        break;
                    case PositionExistsResult.ShortMissing:
                        driftDetail = $"short leg missing from {pos.ShortExchange?.Name}, long leg still present on {pos.LongExchange?.Name}";
                        break;
                    default:
                        continue;
                }

                _logger.LogWarning(
                    "Exchange drift detected for position #{Id} ({Asset} {LongExchange}/{ShortExchange}) — {Detail}",
                    pos.Id, pos.Asset?.Symbol, pos.LongExchange?.Name, pos.ShortExchange?.Name, driftDetail);

                pos.Status = PositionStatus.EmergencyClosed;
                pos.CloseReason = CloseReason.ExchangeDrift;
                pos.ClosedAt = DateTime.UtcNow;

                // For single-leg drift, flag the missing leg before Update so all mutations are captured
                if (result == PositionExistsResult.LongMissing)
                {
                    pos.LongLegClosed = true;
                    survivingLegCloses.Add(pos);
                }
                else if (result == PositionExistsResult.ShortMissing)
                {
                    pos.ShortLegClosed = true;
                    survivingLegCloses.Add(pos);
                }

                _uow.Positions.Update(pos);

                _uow.Alerts.Add(new Alert
                {
                    UserId = pos.UserId,
                    ArbitragePositionId = pos.Id,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"Position #{pos.Id}: {driftDetail} — marked ExchangeDrift. Manual review required.",
                });

                reconciled++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconciliation failed for position #{Id}", pos.Id);
            }
        }

        if (reconciled > 0)
        {
            await _uow.SaveAsync(ct);
            _logger.LogWarning("Reconciliation completed: {Count} positions marked as ExchangeDrift", reconciled);
        }

        // Close surviving legs after SaveAsync commits the drift status (within DI scope lifetime)
        foreach (var pos in survivingLegCloses)
        {
            try
            {
                await _executionEngine.ClosePositionAsync(pos.UserId, pos, CloseReason.ExchangeDrift, ct);
                _logger.LogInformation("Surviving leg close attempted for position #{Id}", pos.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close surviving leg for position #{Id}", pos.Id);
            }
        }
    }
}
