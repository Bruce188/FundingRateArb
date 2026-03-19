using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class PositionHealthMonitor : IPositionHealthMonitor
{
    private readonly IUnitOfWork _uow;
    private readonly IExecutionEngine _executionEngine;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly ILogger<PositionHealthMonitor> _logger;

    public PositionHealthMonitor(
        IUnitOfWork uow,
        IExecutionEngine executionEngine,
        IExchangeConnectorFactory connectorFactory,
        ILogger<PositionHealthMonitor> logger)
    {
        _uow = uow;
        _executionEngine = executionEngine;
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    public async Task CheckAndActAsync(CancellationToken ct = default)
    {
        // M4: Reap stale Opening positions (stuck > 5 minutes)
        await ReapStalePositionsAsync(PositionStatus.Opening, TimeSpan.FromMinutes(5), ct);
        // M4: Reap stale Closing positions (stuck > 10 minutes)
        await ReapStalePositionsAsync(PositionStatus.Closing, TimeSpan.FromMinutes(10), ct);

        // C-PR1: Use tracked query so mutations (CurrentSpreadPerHour) are persisted by EF
        var openPositions = await _uow.Positions.GetOpenTrackedAsync();
        if (openPositions.Count == 0) return;

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

            try
            {
                // Compute price move for stop-loss check
                var longConnector = _connectorFactory.GetConnector(longExchangeName);
                var shortConnector = _connectorFactory.GetConnector(shortExchangeName);

                // H9: Fetch mark prices in parallel instead of sequentially
                var longTask = longConnector.GetMarkPriceAsync(assetSymbol, ct);
                var shortTask = shortConnector.GetMarkPriceAsync(assetSymbol, ct);
                await Task.WhenAll(longTask, shortTask);
                var currentLongMark = await longTask;
                var currentShortMark = await shortTask;

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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check health for position #{Id}: {Message}", pos.Id, ex.Message);
            }
        }

        // C-PH1: Single SaveAsync call after the loop — persists all spread updates and new alerts
        await _uow.SaveAsync(ct);

        // Execute closes after the batch save (spread update is persisted before close)
        foreach (var (pos, reason) in toClose)
        {
            await _executionEngine.ClosePositionAsync(pos, reason, ct);
        }
    }

    private async Task ReapStalePositionsAsync(PositionStatus status, TimeSpan maxAge, CancellationToken ct)
    {
        var positions = await _uow.Positions.GetByStatusAsync(status);
        var cutoff = DateTime.UtcNow - maxAge;

        foreach (var pos in positions.Where(p => p.OpenedAt < cutoff))
        {
            _logger.LogCritical(
                "Reaping stale {Status} position #{PositionId} ({Asset}) — stuck since {OpenedAt}",
                status, pos.Id, pos.Asset?.Symbol ?? "?", pos.OpenedAt);

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
        }

        if (positions.Any(p => p.OpenedAt < cutoff))
            await _uow.SaveAsync(ct);
    }
}
