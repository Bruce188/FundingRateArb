using System.Collections.Concurrent;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class PositionHealthMonitor : IPositionHealthMonitor
{
    private readonly IUnitOfWork _uow;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IMarketDataCache _marketDataCache;
    private readonly IExecutionEngine _executionEngine;
    private readonly ILogger<PositionHealthMonitor> _logger;
    private readonly ConcurrentDictionary<int, int> _priceFetchFailures = new();

    public PositionHealthMonitor(
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        IMarketDataCache marketDataCache,
        IExecutionEngine executionEngine,
        ILogger<PositionHealthMonitor> logger)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
        _marketDataCache = marketDataCache;
        _executionEngine = executionEngine;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckAndActAsync(CancellationToken ct = default)
    {
        var allReaped = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId)>();

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
            return new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                allReaped);
        }

        var config = await _uow.BotConfig.GetActiveAsync();
        var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();

        // H3: Build a dictionary for O(1) lookup instead of O(N*M) linear scan
        var rateMap = latestRates.ToDictionary(r => (r.ExchangeId, r.AssetId));

        // C-PH1: Collect positions that need closing; call SaveAsync ONCE after the loop
        var toClose = new List<(ArbitragePosition Position, CloseReason Reason)>();

        foreach (var pos in openPositions)
        {
            var assetSymbol = pos.Asset?.Symbol ?? "?";
            var longExchangeName = pos.LongExchange?.Name ?? "?";
            var shortExchangeName = pos.ShortExchange?.Name ?? "?";

            // Compute current spread from latest funding rate snapshots (O(1) dictionary lookup)
            rateMap.TryGetValue((pos.LongExchangeId, pos.AssetId), out var longRate);
            rateMap.TryGetValue((pos.ShortExchangeId, pos.AssetId), out var shortRate);

            var spread = (shortRate?.RatePerHour ?? 0m) - (longRate?.RatePerHour ?? 0m);

            // Always update current spread (tracked entity — change recorded by EF)
            pos.CurrentSpreadPerHour = spread;
            _uow.Positions.Update(pos);

            if (pos.LongEntryPrice <= 0 || pos.ShortEntryPrice <= 0)
            {
                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    _logger.LogCritical("Position #{Id} has zero entry prices — stop-loss check disabled, skipping", pos.Id);
                }
                continue;
            }

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

                var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;

                // Determine close reason (priority: stop-loss > PnL target > max hold > spread collapsed)
                var reason = DetermineCloseReason(pos, config, unrealizedPnl, hoursOpen, spread);

                if (reason.HasValue)
                {
                    _logger.LogWarning(
                        "Auto-closing position #{PositionId}: {Asset} " +
                        "reason={CloseReason}, spread={Spread}/hour, " +
                        "hoursOpen={HoursOpen:F1}, unrealizedPnl={UnrealizedPnl:F2}",
                        pos.Id, assetSymbol, reason.Value,
                        spread, hoursOpen, unrealizedPnl);

                    toClose.Add((pos, reason.Value));
                    continue; // skip alert check — position will be closed
                }

                // Alert if spread below alert threshold (but above close threshold)
                if (spread < config.AlertThreshold)
                {
                    var recentAlert = await _uow.Alerts.GetRecentAsync(
                        pos.UserId, pos.Id, AlertType.SpreadWarning, TimeSpan.FromHours(1));

                    if (recentAlert is null)
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

                if (failures >= 5)
                {
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = pos.UserId,
                        ArbitragePositionId = pos.Id,
                        Type = AlertType.PriceFeedFailure,
                        Severity = AlertSeverity.Critical,
                        Message = $"Price feed failed {failures} consecutive times for " +
                                  $"{pos.Asset?.Symbol ?? "unknown"}. " +
                                  "Stop-loss protection is INACTIVE. Consider manual close.",
                    });
                }
            }
        }

        // C-PH1: Single SaveAsync call after the loop — persists all spread updates and new alerts
        await _uow.SaveAsync(ct);

        return new HealthCheckResult(toClose, allReaped);
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

    private async Task<(IReadOnlyList<ArbitragePosition> NonReaped, List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId)> Reaped)> ReapStaleClosingPositionsAsync(
        IReadOnlyList<ArbitragePosition> closingPositions, TimeSpan maxAge, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var nonReaped = new List<ArbitragePosition>();
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId)>();

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
            reapedPositions.Add((pos.Id, pos.UserId, pos.LongExchangeId, pos.ShortExchangeId));
        }

        if (reapedPositions.Count > 0)
        {
            await _uow.SaveAsync(ct);
        }

        return (nonReaped, reapedPositions);
    }

    private async Task<List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId)>> ReapStalePositionsAsync(PositionStatus status, TimeSpan maxAge, CancellationToken ct)
    {
        var positions = await _uow.Positions.GetByStatusAsync(status);
        var cutoff = DateTime.UtcNow - maxAge;
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId)>();

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
            reapedPositions.Add((pos.Id, pos.UserId, pos.LongExchangeId, pos.ShortExchangeId));
        }

        if (reapedPositions.Count > 0)
        {
            await _uow.SaveAsync(ct);
        }

        return reapedPositions;
    }

    public static CloseReason? DetermineCloseReason(
        ArbitragePosition pos, BotConfiguration config,
        decimal unrealizedPnl, decimal hoursOpen, decimal spread)
    {
        // Priority: StopLoss > PnlTargetReached > MaxHoldTime > SpreadCollapsed
        if (pos.MarginUsdc > 0 && unrealizedPnl < 0 && Math.Abs(unrealizedPnl) >= config.StopLossPct * pos.MarginUsdc)
        {
            return CloseReason.StopLoss;
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
                return CloseReason.PnlTargetReached;
            }
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
}
