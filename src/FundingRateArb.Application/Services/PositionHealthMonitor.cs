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

    public async Task CheckAndActAsync()
    {
        var openPositions = await _uow.Positions.GetOpenAsync();
        if (openPositions.Count == 0) return;

        var config = await _uow.BotConfig.GetActiveAsync();
        var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();

        foreach (var pos in openPositions)
        {
            var assetSymbol = pos.Asset?.Symbol ?? "?";
            var longExchangeName = pos.LongExchange?.Name ?? "?";
            var shortExchangeName = pos.ShortExchange?.Name ?? "?";

            // Compute current spread from latest funding rate snapshots
            var longRate = latestRates
                .FirstOrDefault(r => r.ExchangeId == pos.LongExchangeId && r.AssetId == pos.AssetId);
            var shortRate = latestRates
                .FirstOrDefault(r => r.ExchangeId == pos.ShortExchangeId && r.AssetId == pos.AssetId);

            var spread = (shortRate?.RatePerHour ?? 0m) - (longRate?.RatePerHour ?? 0m);

            // Always update current spread
            pos.CurrentSpreadPerHour = spread;
            _uow.Positions.Update(pos);

            // Compute price move for stop-loss check
            var longConnector = _connectorFactory.GetConnector(longExchangeName);
            var shortConnector = _connectorFactory.GetConnector(shortExchangeName);

            // H9: Fetch mark prices in parallel instead of sequentially
            var longTask = longConnector.GetMarkPriceAsync(assetSymbol);
            var shortTask = shortConnector.GetMarkPriceAsync(assetSymbol);
            await Task.WhenAll(longTask, shortTask);
            var currentLongMark = await longTask;
            var currentShortMark = await shortTask;

            var avgEntryPrice   = (pos.LongEntryPrice + pos.ShortEntryPrice) / 2m;
            var avgCurrentPrice = (currentLongMark + currentShortMark) / 2m;
            var priceMove = avgEntryPrice > 0
                ? Math.Abs(avgCurrentPrice - avgEntryPrice) / avgEntryPrice
                : 0m;

            var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;

            // Determine close reason (priority: stop-loss > max hold > spread collapsed)
            CloseReason? reason = null;

            if (priceMove >= config.StopLossPct)
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
                    "hoursOpen={HoursOpen:F1}, priceMove={PriceMove:P2}",
                    pos.Id, assetSymbol, reason.Value,
                    spread, hoursOpen, priceMove);

                await _uow.SaveAsync(); // persist spread update before close
                await _executionEngine.ClosePositionAsync(pos, reason.Value);
                continue; // skip alert check — position is closed
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

            await _uow.SaveAsync();
        }
    }
}
