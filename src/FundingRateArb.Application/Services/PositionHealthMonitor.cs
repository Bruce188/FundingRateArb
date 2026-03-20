using System.Collections.Concurrent;
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
    private readonly ILogger<PositionHealthMonitor> _logger;
    private readonly ConcurrentDictionary<int, int> _priceFetchFailures = new();

    public PositionHealthMonitor(
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        IMarketDataCache marketDataCache,
        ILogger<PositionHealthMonitor> logger)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
        _marketDataCache = marketDataCache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<(ArbitragePosition Position, CloseReason Reason)>> CheckAndActAsync(CancellationToken ct = default)
    {
        // M4: Reap stale Opening positions (stuck > 5 minutes)
        await ReapStalePositionsAsync(PositionStatus.Opening, TimeSpan.FromMinutes(5), ct);
        // M4: Reap stale Closing positions (stuck > 10 minutes)
        await ReapStalePositionsAsync(PositionStatus.Closing, TimeSpan.FromMinutes(10), ct);

        // C-PR1: Use tracked query so mutations (CurrentSpreadPerHour) are persisted by EF
        var openPositions = await _uow.Positions.GetOpenTrackedAsync();
        if (openPositions.Count == 0) return Array.Empty<(ArbitragePosition, CloseReason)>();

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
                _logger.LogCritical("Position #{Id} has zero entry prices — stop-loss check disabled, skipping", pos.Id);
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
                var longPnl  = (currentLongMark - pos.LongEntryPrice) * estimatedQty;
                var shortPnl = (pos.ShortEntryPrice - currentShortMark) * estimatedQty;
                var unrealizedPnl = longPnl + shortPnl;

                var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;

                // Determine close reason (priority: stop-loss > max hold > spread collapsed)
                CloseReason? reason = null;

                if (pos.MarginUsdc > 0 && unrealizedPnl < 0 && Math.Abs(unrealizedPnl) >= config.StopLossPct * pos.MarginUsdc)
                    reason = CloseReason.StopLoss;
                else if (hoursOpen >= config.MaxHoldTimeHours)
                    reason = CloseReason.MaxHoldTimeReached;
                else if (spread < config.CloseThreshold)
                    reason = CloseReason.SpreadCollapsed;

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
                            UserId              = pos.UserId,
                            ArbitragePositionId = pos.Id,
                            Type                = AlertType.SpreadWarning,
                            Severity            = AlertSeverity.Warning,
                            Message             = $"Spread warning: {assetSymbol} " +
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

        return toClose;
    }

    private async Task ReapStalePositionsAsync(PositionStatus status, TimeSpan maxAge, CancellationToken ct)
    {
        var positions = await _uow.Positions.GetByStatusAsync(status);
        var cutoff = DateTime.UtcNow - maxAge;

        var reaped = false;
        foreach (var pos in positions)
        {
            // For Closing status, use ClosingStartedAt; for Opening, use OpenedAt
            var referenceTime = status == PositionStatus.Closing
                ? pos.ClosingStartedAt ?? pos.OpenedAt
                : pos.OpenedAt;

            if (referenceTime >= cutoff) continue;

            _logger.LogCritical(
                "Reaping stale {Status} position #{PositionId} ({Asset}) — stuck since {OpenedAt}",
                status, pos.Id, pos.Asset?.Symbol ?? "?", referenceTime);

            pos.Status = PositionStatus.EmergencyClosed;
            _uow.Positions.Update(pos);
            _uow.Alerts.Add(new Alert
            {
                UserId   = pos.UserId,
                ArbitragePositionId = pos.Id,
                Type     = AlertType.LegFailed,
                Severity = AlertSeverity.Critical,
                Message  = $"Position #{pos.Id} stuck in {status} for >{maxAge.TotalMinutes:F0} minutes. " +
                           $"Auto-transitioned to EmergencyClosed. Manual intervention required.",
            });
            reaped = true;
        }

        if (reaped)
            await _uow.SaveAsync(ct);
    }
}
