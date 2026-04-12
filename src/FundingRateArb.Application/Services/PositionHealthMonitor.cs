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
    private const string BinanceExchangeName = "Binance";

    private readonly IUnitOfWork _uow;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IMarketDataCache _marketDataCache;
    private readonly IReferencePriceProvider _referencePriceProvider;
    private readonly IExecutionEngine _executionEngine;
    private readonly ILeverageTierProvider _tierProvider;
    private readonly IHealthMonitorState _state;
    private readonly ILogger<PositionHealthMonitor> _logger;
    private readonly ConcurrentDictionary<int, DateTime> _recentMarginAlerts = new();

    public PositionHealthMonitor(
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        IMarketDataCache marketDataCache,
        IReferencePriceProvider referencePriceProvider,
        IExecutionEngine executionEngine,
        ILeverageTierProvider tierProvider,
        IHealthMonitorState state,
        ILogger<PositionHealthMonitor> logger)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
        _marketDataCache = marketDataCache;
        _referencePriceProvider = referencePriceProvider;
        _executionEngine = executionEngine;
        _tierProvider = tierProvider;
        _state = state;
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
            _state.NegativeFundingCycles.Clear();
            _state.PriceFetchFailures.Clear();
            _state.ZeroPriceCheckCounts.Clear();

            return new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                allReaped,
                new Dictionary<int, ComputedPositionPnl>());
        }

        var config = await _uow.BotConfig.GetActiveAsync();

        // Stablecoin depeg check — before position loop (every 5th cycle; USDCUSDT is slow-moving)
        var stablecoinCritical = false;
        if (_state.ShouldCheckStablecoin(5))
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
                _state.NegativeFundingCycles.AddOrUpdate(pos.Id, 1, (_, c) => c + 1);
            }
            else
            {
                _state.NegativeFundingCycles.TryRemove(pos.Id, out _);
            }

            // Stablecoin depeg: close cross-stablecoin positions when critical
            if (stablecoinCritical)
            {
                var longExName = pos.LongExchange?.Name ?? exchangeNameById.GetValueOrDefault(pos.LongExchangeId);
                var shortExName = pos.ShortExchange?.Name ?? exchangeNameById.GetValueOrDefault(pos.ShortExchangeId);
                var isCrossStablecoin = (longExName == BinanceExchangeName) != (shortExName == BinanceExchangeName);
                if (isCrossStablecoin)
                {
                    _logger.LogWarning("Closing cross-stablecoin position #{Id}: stablecoin depeg critical", pos.Id);
                    toClose.Add((pos, CloseReason.StablecoinDepeg));
                    continue;
                }
            }

            if (pos.LongEntryPrice <= 0 || pos.ShortEntryPrice <= 0)
            {
                var checkCount = _state.ZeroPriceCheckCounts.AddOrUpdate(pos.Id, 1, (_, c) => c + 1);
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
                    _state.ZeroPriceCheckCounts[pos.Id] = 0;
                }
                continue;
            }
            // Clean up tracking for positions that now have valid prices
            _state.ZeroPriceCheckCounts.TryRemove(pos.Id, out _);

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

                // Unified PnL: both legs valued against single reference price
                var unifiedPrice = _referencePriceProvider.GetUnifiedPrice(assetSymbol, longExchangeName, shortExchangeName);
                decimal unifiedUnrealizedPnl;
                decimal unifiedLongPnl, unifiedShortPnl;
                if (unifiedPrice > 0)
                {
                    unifiedLongPnl = (unifiedPrice - pos.LongEntryPrice) * estimatedQty;
                    unifiedShortPnl = (pos.ShortEntryPrice - unifiedPrice) * estimatedQty;
                    unifiedUnrealizedPnl = unifiedLongPnl + unifiedShortPnl;
                }
                else
                {
                    // Unified price unavailable (data feed gap) — fall back to per-exchange PnL
                    _logger.LogWarning(
                        "Unified price unavailable for {Asset} ({Long}/{Short}), falling back to per-exchange PnL",
                        assetSymbol, longExchangeName, shortExchangeName);
                    unifiedUnrealizedPnl = unrealizedPnl;
                    unifiedLongPnl = longPnl;
                    unifiedShortPnl = shortPnl;
                }

                // Collateral imbalance monitoring — uses unified PnL to avoid
                // spurious alerts from cross-exchange price divergence
                var marginPerLeg = pos.MarginUsdc / 2m;
                decimal? collateralImbalancePct = null;
                if (marginPerLeg > 0)
                {
                    var longUtil = Math.Abs(unifiedLongPnl) / marginPerLeg;
                    var shortUtil = Math.Abs(unifiedShortPnl) / marginPerLeg;
                    collateralImbalancePct = Math.Abs(longUtil - shortUtil);

                    if (collateralImbalancePct > 0.30m)
                    {
                        var winningExchange = unifiedLongPnl > unifiedShortPnl ? longExchangeName : shortExchangeName;
                        var losingExchange = unifiedLongPnl > unifiedShortPnl ? shortExchangeName : longExchangeName;

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

                // Fetch margin state from exchange APIs once per cycle.
                // Reused by liquidation distance computation AND margin utilization check
                // to avoid duplicate API calls.
                var (longMargin, shortMargin) = await FetchMarginStateAsync(
                    longExchangeName, shortExchangeName, assetSymbol, ct);

                // Calculate liquidation prices and distance.
                // Prefer API-pulled liquidation prices (authoritative per-exchange) when available,
                // falling back to the leverage formula only if the exchange could not supply them.
                var minLiquidationDistance = ComputeLiquidationDistance(
                    pos,
                    currentLongMark,
                    currentShortMark,
                    longMargin?.LiquidationPrice,
                    shortMargin?.LiquidationPrice);

                var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;

                // Determine close reason (priority: stop-loss > liquidation > PnL target > max hold > spread collapsed)
                // Intentional: all close reasons including PnlTargetReached use unified PnL (strategy-level profit
                // target, not per-exchange margin impact). This ensures consistent close logic against a single
                // reference price, avoiding false triggers from cross-exchange price divergence.
                _state.NegativeFundingCycles.TryGetValue(pos.Id, out var flipCount);
                var entryMidForClose = (pos.LongEntryPrice + pos.ShortEntryPrice) / 2m;
                var entrySpreadCostPctForClose = entryMidForClose > 0
                    ? Math.Abs(pos.ShortEntryPrice - pos.LongEntryPrice) / entryMidForClose * 100m
                    : 0m;
                var reason = DetermineCloseReason(
                    pos, config, unifiedUnrealizedPnl, hoursOpen, spread,
                    minLiquidationDistance, flipCount, entrySpreadCostPctForClose);

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

                // Liquidation early warning — fires when the remaining safe range has dropped
                // below the configured early-warning threshold (default 0.75 = 25% consumed).
                // This is strictly looser than the close trigger at LiquidationWarningPct (0.50)
                // so ops see a warning before the position is auto-closed.
                //
                // Defensive clamp: if an operator misconfigures LiquidationEarlyWarningPct to a
                // value <= LiquidationWarningPct, the warning would never fire before the close
                // path preempts it. Floor the effective threshold so the warning at least fires
                // at the close boundary and ops get a signal on the same cycle.
                var effectiveEarlyWarningPct = Math.Max(
                    config.LiquidationEarlyWarningPct, config.LiquidationWarningPct);

                if (minLiquidationDistance.HasValue && minLiquidationDistance.Value < effectiveEarlyWarningPct)
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
                                      $"distance={minLiquidationDistance.Value:P1} (threshold={effectiveEarlyWarningPct:P1})",
                        });
                    }
                }

                // Margin utilization alerts — reuse the margin state fetched above.
                CheckMarginUtilization(pos, config, longExchangeName, shortExchangeName, assetSymbol, longMargin, shortMargin);

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

                _state.PriceFetchFailures.TryRemove(pos.Id, out _);
            }
            catch (Exception ex)
            {
                var failures = _state.PriceFetchFailures.AddOrUpdate(pos.Id, 1, (_, v) => v + 1);
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
        foreach (var key in _state.NegativeFundingCycles.Keys)
        {
            if (!openIds.Contains(key))
            {
                _state.NegativeFundingCycles.TryRemove(key, out _);
            }
        }

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
        int negativeFundingCycles = 0,
        decimal entrySpreadCostPct = 0m)
    {
        // Priority (top wins):
        //   StopLoss > LiquidationRisk > DivergenceCritical > PnlTargetReached(with divergence deferral)
        //   > SpreadCollapsed(emergency path, bypasses MinHoldTime) > FundingFlipped > MaxHoldTimeReached
        //   > SpreadCollapsed(normal path, respects MinHoldTime)
        // Note: both SpreadCollapsed returns use the same enum value; downstream alerts can
        // distinguish by checking the spread magnitude against EmergencyCloseSpreadThreshold.
        if (pos.MarginUsdc > 0 && unrealizedPnl < 0 && Math.Abs(unrealizedPnl) >= config.StopLossPct * pos.MarginUsdc)
        {
            return CloseReason.StopLoss;
        }

        if (minLiquidationDistance.HasValue && minLiquidationDistance.Value < config.LiquidationWarningPct)
        {
            return CloseReason.LiquidationRisk;
        }

        // Critical divergence close per Analysis Section 4.6: when live mark-price spread between
        // the two exchanges exceeds 2× the alert threshold (so 4× entry spread cost by default),
        // the basis cost to exit will dominate any remaining funding income. Profitability-fixes
        // F4: in a delta-neutral hedge, mark-price divergence alone is NOT a closure signal — the
        // real risks are liquidation, funding sign flip, and leg drift. So when
        // UseRiskBasedDivergenceClose is true (default), we require both a minimum hold window
        // and an unhealthy liquidation distance (below LiquidationEarlyWarningPct) before
        // escalating to a close. The old threshold-only behavior is preserved behind the
        // feature flag for rollback safety.
        var currentDivergencePct = pos.CurrentDivergencePct ?? 0m;
        var divergenceCloseThreshold = entrySpreadCostPct > 0m
            ? entrySpreadCostPct * config.DivergenceAlertMultiplier * 2m
            : 0m;
        if (divergenceCloseThreshold > 0m && currentDivergencePct > divergenceCloseThreshold)
        {
            if (!config.UseRiskBasedDivergenceClose)
            {
                // Legacy behavior: fire immediately on threshold breach.
                return CloseReason.DivergenceCritical;
            }

            var pastMinHoldTime = hoursOpen >= (decimal)config.MinHoldTimeHours;
            var liquidationUnhealthy = minLiquidationDistance.HasValue
                && minLiquidationDistance.Value < config.LiquidationEarlyWarningPct;
            if (pastMinHoldTime && liquidationUnhealthy)
            {
                return CloseReason.DivergenceCritical;
            }
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
                    // Defer closing when divergence is above the alert threshold but not yet
                    // critical — waiting for convergence reduces exit slippage and preserves
                    // funding income already collected. Only skip the deferral if funding income
                    // has accumulated far beyond the target (3× target means the position has
                    // over-performed and extra hold risk is no longer justified).
                    var divergenceAlertLevel = entrySpreadCostPct > 0m
                        ? entrySpreadCostPct * config.DivergenceAlertMultiplier
                        : 0m;
                    var hasOverperformed = pos.AccumulatedFunding >= 3m * config.TargetPnlMultiplier * entryFee;
                    if (divergenceAlertLevel > 0m
                        && currentDivergencePct > divergenceAlertLevel
                        && !hasOverperformed)
                    {
                        // Defer — wait for convergence
                    }
                    else
                    {
                        return CloseReason.PnlTargetReached;
                    }
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
    /// as a fraction of the safe range (0.0 = at liquidation, 1.0 = entry-level).
    /// Uses API-pulled liquidation prices when the exchange provides them
    /// (Hyperliquid clearinghouseState, Binance positionRisk, Lighter account, Aster positionRisk);
    /// falls back to the leverage formula only for exchanges that cannot supply them.
    /// Updates LongLiquidationPrice and ShortLiquidationPrice on the position with the best
    /// available value. Returns null if leverage is zero or entry prices are invalid.
    /// </summary>
    public static decimal? ComputeLiquidationDistance(
        ArbitragePosition pos,
        decimal currentLongMark,
        decimal currentShortMark,
        decimal? apiLongLiqPrice = null,
        decimal? apiShortLiqPrice = null)
    {
        if (pos.Leverage <= 0 || pos.LongEntryPrice <= 0 || pos.ShortEntryPrice <= 0)
        {
            return null;
        }

        // Prefer API-pulled liquidation prices (authoritative per-exchange margin engine)
        // over the leverage-formula approximation. The formula ignores cross-margin, isolated
        // buffers, maintenance margin tiers, and accumulated funding — all of which shift the
        // real liquidation boundary. Fall back to the formula only when the exchange cannot
        // supply a liquidation price.
        var longLiqPrice = apiLongLiqPrice is > 0m
            ? apiLongLiqPrice.Value
            : pos.LongEntryPrice * (1m - 1m / pos.Leverage);
        var shortLiqPrice = apiShortLiqPrice is > 0m
            ? apiShortLiqPrice.Value
            : pos.ShortEntryPrice * (1m + 1m / pos.Leverage);

        pos.LongLiquidationPrice = longLiqPrice;
        pos.ShortLiquidationPrice = shortLiqPrice;

        // Distance from current mark to liquidation as fraction of entry-to-liquidation range.
        // Using CURRENT mark (not entry) as the numerator reflects the actual remaining buffer
        // as price moves — the safe range shrinks as the position drifts toward its liquidation.
        //
        // When the API-pulled liquidation price has crossed the entry price (e.g., accumulated
        // PnL or funding has shifted the cross-margin liquidation boundary past entry), the
        // entry-to-liquidation range becomes non-positive. That position is at or past its
        // safe range and must be treated as immediate liquidation risk — NOT as
        // "infinitely safe" (which the previous decimal.MaxValue fallback implied).
        var longRange = pos.LongEntryPrice - longLiqPrice;
        var shortRange = shortLiqPrice - pos.ShortEntryPrice;

        var longDistance = longRange > 0
            ? (currentLongMark - longLiqPrice) / longRange
            : 0m;
        var shortDistance = shortRange > 0
            ? (shortLiqPrice - currentShortMark) / shortRange
            : 0m;

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

    /// <summary>
    /// Fetches live margin state for both legs in a single dispatch. Returns (null, null)
    /// on API failure — callers must handle the null case by falling back to local formulas.
    /// </summary>
    private async Task<(MarginStateDto? LongMargin, MarginStateDto? ShortMargin)> FetchMarginStateAsync(
        string longExchangeName, string shortExchangeName, string assetSymbol, CancellationToken ct)
    {
        try
        {
            var longConnector = _connectorFactory.GetConnector(longExchangeName);
            var shortConnector = _connectorFactory.GetConnector(shortExchangeName);

            if (!longConnector.HasCredentials || !shortConnector.HasCredentials)
            {
                _logger.LogDebug(
                    "Skipping margin state fetch for {Asset}: connector missing credentials ({Long}={LongOk}, {Short}={ShortOk})",
                    assetSymbol,
                    longExchangeName, longConnector.HasCredentials,
                    shortExchangeName, shortConnector.HasCredentials);
                return (null, null);
            }

            // Deduplicate: if both legs use the same connector for the same asset, call once.
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

            return (await longMarginTask, await shortMarginTask);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Margin state fetch failed for {Asset} on {Long}/{Short}",
                assetSymbol, longExchangeName, shortExchangeName);
            return (null, null);
        }
    }

    private void CheckMarginUtilization(
        ArbitragePosition pos, BotConfiguration config,
        string longExchangeName, string shortExchangeName, string assetSymbol,
        MarginStateDto? longMargin, MarginStateDto? shortMargin)
    {
        try
        {
            // Check if either leg exceeds the margin utilization alert threshold.
            var maxUtilization = Math.Max(
                longMargin?.MarginUtilizationPct ?? 0m,
                shortMargin?.MarginUtilizationPct ?? 0m);

            // Split threshold per Analysis Section 7.4: at >=3x leverage, cap the alert
            // threshold at 60% even if the operator's configured MarginUtilizationAlertPct
            // is looser; at <3x leverage, use the operator's setting as-is. If the operator
            // tightens the config to e.g. 0.50m, the stricter value always wins at high
            // leverage via Math.Min. Reason: tighter buffer before liquidation at higher
            // leverage, so we want to alert earlier.
            var leverageAwareThreshold = pos.Leverage >= 3
                ? Math.Min(config.MarginUtilizationAlertPct, 0.60m)
                : config.MarginUtilizationAlertPct;

            if (maxUtilization >= leverageAwareThreshold)
            {
                // NB12: In-memory dedup before hitting the DB
                if (_recentMarginAlerts.TryGetValue(pos.Id, out var lastAlertTime)
                    && DateTime.UtcNow - lastAlertTime < TimeSpan.FromHours(1))
                {
                    return;
                }

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
                              $"(threshold={leverageAwareThreshold:P0}, {pos.Leverage}x leverage)",
                });

                _recentMarginAlerts[pos.Id] = DateTime.UtcNow;
            }
        }
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
            var binance = _connectorFactory.GetConnector(BinanceExchangeName);
            var price = await binance.GetMarkPriceAsync("USDCUSDT", ct);
            if (price <= 0)
            {
                return false;
            }

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

    public Task<ComputedPositionPnl?> ComputePositionSnapshotAsync(ArbitragePosition position, CancellationToken ct = default)
    {
        var assetSymbol = position.Asset?.Symbol ?? "?";
        var longExchangeName = position.LongExchange?.Name ?? "?";
        var shortExchangeName = position.ShortExchange?.Name ?? "?";

        if (position.LongEntryPrice <= 0 || position.ShortEntryPrice <= 0)
        {
            return Task.FromResult<ComputedPositionPnl?>(null);
        }

        var currentLongMark = _marketDataCache.GetMarkPrice(longExchangeName, assetSymbol);
        var currentShortMark = _marketDataCache.GetMarkPrice(shortExchangeName, assetSymbol);

        if (currentLongMark <= 0 || currentShortMark <= 0)
        {
            // Cache cold — caller falls back to zero-PnL behavior
            return Task.FromResult<ComputedPositionPnl?>(null);
        }

        var avgEntryPrice = (position.LongEntryPrice + position.ShortEntryPrice) / 2m;
        var estimatedQty = avgEntryPrice > 0
            ? position.SizeUsdc * position.Leverage / avgEntryPrice
            : 0m;
        var longPnl = (currentLongMark - position.LongEntryPrice) * estimatedQty;
        var shortPnl = (position.ShortEntryPrice - currentShortMark) * estimatedQty;
        var exchangePnl = longPnl + shortPnl;

        var unifiedPrice = _referencePriceProvider.GetUnifiedPrice(assetSymbol, longExchangeName, shortExchangeName);
        decimal unifiedPnl;
        decimal unifiedLongPnl, unifiedShortPnl;
        if (unifiedPrice > 0)
        {
            unifiedLongPnl = (unifiedPrice - position.LongEntryPrice) * estimatedQty;
            unifiedShortPnl = (position.ShortEntryPrice - unifiedPrice) * estimatedQty;
            unifiedPnl = unifiedLongPnl + unifiedShortPnl;
        }
        else
        {
            unifiedPnl = exchangePnl;
            unifiedLongPnl = longPnl;
            unifiedShortPnl = shortPnl;
        }

        decimal? collateralImbalancePct = null;
        var marginPerLeg = position.MarginUsdc / 2m;
        if (marginPerLeg > 0)
        {
            var longUtil = Math.Abs(unifiedLongPnl) / marginPerLeg;
            var shortUtil = Math.Abs(unifiedShortPnl) / marginPerLeg;
            collateralImbalancePct = Math.Abs(longUtil - shortUtil);
        }

        var divergencePct = unifiedPrice > 0
            ? Math.Abs(currentLongMark - currentShortMark) / unifiedPrice * 100m
            : 0m;

        return Task.FromResult<ComputedPositionPnl?>(new ComputedPositionPnl(
            ExchangePnl: exchangePnl,
            UnifiedPnl: unifiedPnl,
            DivergencePct: divergencePct,
            CollateralImbalancePct: collateralImbalancePct));
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
