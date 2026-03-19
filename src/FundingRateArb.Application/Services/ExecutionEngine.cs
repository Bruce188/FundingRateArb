using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class ExecutionEngine : IExecutionEngine
{
    private readonly IUnitOfWork _uow;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly ILogger<ExecutionEngine> _logger;

    public ExecutionEngine(
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        ILogger<ExecutionEngine> logger)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> OpenPositionAsync(
        ArbitrageOpportunityDto opp, decimal sizeUsdc, CancellationToken ct = default)
    {
        var config = await _uow.BotConfig.GetActiveAsync();

        var longConnector  = _connectorFactory.GetConnector(opp.LongExchangeName);
        var shortConnector = _connectorFactory.GetConnector(opp.ShortExchangeName);

        _logger.LogInformation(
            "Opening position: {Asset} Long={LongExchange} Short={ShortExchange} Size={Size} USDC",
            opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, sizeUsdc);

        // C-EE2: Persist a sentinel record BEFORE firing legs so that a crash after one leg
        // succeeds leaves a recoverable audit trail.
        var position = new ArbitragePosition
        {
            UserId               = config.UpdatedByUserId,
            AssetId              = opp.AssetId,
            LongExchangeId       = opp.LongExchangeId,
            ShortExchangeId      = opp.ShortExchangeId,
            SizeUsdc             = sizeUsdc,
            MarginUsdc           = sizeUsdc / config.DefaultLeverage,
            Leverage             = config.DefaultLeverage,
            EntrySpreadPerHour   = opp.SpreadPerHour,
            CurrentSpreadPerHour = opp.SpreadPerHour,
            Status               = PositionStatus.Opening,
            OpenedAt             = DateTime.UtcNow,
        };

        _uow.Positions.Add(position);
        await _uow.SaveAsync(ct);

        // Place both legs concurrently
        var longTask  = longConnector.PlaceMarketOrderAsync(opp.AssetSymbol, Side.Long,  sizeUsdc, config.DefaultLeverage, ct);
        var shortTask = shortConnector.PlaceMarketOrderAsync(opp.AssetSymbol, Side.Short, sizeUsdc, config.DefaultLeverage, ct);

        await Task.WhenAll(longTask, shortTask);

        var longResult  = longTask.Result;
        var shortResult = shortTask.Result;

        if (longResult.Success && shortResult.Success)
        {
            position.LongEntryPrice  = longResult.FilledPrice;
            position.ShortEntryPrice = shortResult.FilledPrice;
            position.LongOrderId     = longResult.OrderId;
            position.ShortOrderId    = shortResult.OrderId;
            position.Status          = PositionStatus.Open;

            _uow.Positions.Update(position);
            _uow.Alerts.Add(new Alert
            {
                UserId   = config.UpdatedByUserId,
                Type     = AlertType.PositionOpened,
                Severity = AlertSeverity.Info,
                Message  = $"Position opened: {opp.AssetSymbol} " +
                           $"Long/{opp.LongExchangeName} Short/{opp.ShortExchangeName} " +
                           $"@ {sizeUsdc:F2} USDC",
            });

            await _uow.SaveAsync(ct);

            _logger.LogInformation(
                "Position opened: {Asset} LongOrderId={LongOrderId} ShortOrderId={ShortOrderId}",
                opp.AssetSymbol, longResult.OrderId, shortResult.OrderId);

            return (true, null);
        }

        // Emergency close — close any leg that succeeded, independently fault-tolerant
        _logger.LogError(
            "EMERGENCY CLOSE — One leg failed: {Asset} Long={LongStatus} Short={ShortStatus}",
            opp.AssetSymbol,
            longResult.Success  ? "OK" : $"FAILED: {longResult.Error}",
            shortResult.Success ? "OK" : $"FAILED: {shortResult.Error}");

        if (longResult.Success)
        {
            try { await longConnector.ClosePositionAsync(opp.AssetSymbol, Side.Long, ct); }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "EMERGENCY CLOSE FAILED for long leg: {Asset}", opp.AssetSymbol);
                _uow.Alerts.Add(new Alert
                {
                    UserId   = config.UpdatedByUserId,
                    Type     = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message  = $"EMERGENCY CLOSE FAILED — long leg {opp.AssetSymbol} could not be closed. Manual intervention required.",
                });
            }
        }
        if (shortResult.Success)
        {
            try { await shortConnector.ClosePositionAsync(opp.AssetSymbol, Side.Short, ct); }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "EMERGENCY CLOSE FAILED for short leg: {Asset}", opp.AssetSymbol);
                _uow.Alerts.Add(new Alert
                {
                    UserId   = config.UpdatedByUserId,
                    Type     = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message  = $"EMERGENCY CLOSE FAILED — short leg {opp.AssetSymbol} could not be closed. Manual intervention required.",
                });
            }
        }

        _uow.Alerts.Add(new Alert
        {
            UserId   = config.UpdatedByUserId,
            Type     = AlertType.LegFailed,
            Severity = AlertSeverity.Critical,
            Message  = $"Emergency close: {opp.AssetSymbol} " +
                       $"Long={longResult.Error ?? "OK"} Short={shortResult.Error ?? "OK"}",
        });

        // Mark the pre-persisted sentinel as EmergencyClosed so the position is not orphaned
        position.Status = PositionStatus.EmergencyClosed;
        _uow.Positions.Update(position);

        await _uow.SaveAsync(ct);

        var error = longResult.Error ?? shortResult.Error ?? "Both legs failed";
        return (false, error);
    }

    public async Task ClosePositionAsync(ArbitragePosition position, CloseReason reason, CancellationToken ct = default)
    {
        // Resolve exchange names (use nav property if loaded, else query DB)
        var longExchangeName  = position.LongExchange?.Name
            ?? (await _uow.Exchanges.GetByIdAsync(position.LongExchangeId))!.Name;
        var shortExchangeName = position.ShortExchange?.Name
            ?? (await _uow.Exchanges.GetByIdAsync(position.ShortExchangeId))!.Name;
        var assetSymbol = position.Asset?.Symbol
            ?? (await _uow.Assets.GetByIdAsync(position.AssetId))!.Symbol;

        var longConnector  = _connectorFactory.GetConnector(longExchangeName);
        var shortConnector = _connectorFactory.GetConnector(shortExchangeName);

        _logger.LogInformation(
            "Closing position #{PositionId}: {Asset} reason={Reason}",
            position.Id, assetSymbol, reason);

        position.Status = PositionStatus.Closing;
        _uow.Positions.Update(position);
        await _uow.SaveAsync(ct);

        // Close both legs concurrently to minimize directional exposure between fills
        var longCloseTask  = longConnector.ClosePositionAsync(assetSymbol, Side.Long, ct);
        var shortCloseTask = shortConnector.ClosePositionAsync(assetSymbol, Side.Short, ct);
        await Task.WhenAll(longCloseTask, shortCloseTask);
        var longClose  = longCloseTask.Result;
        var shortClose = shortCloseTask.Result;

        // C-EE1: Compute each leg independently so differing fill quantities produce correct PnL.
        var longPnl  = (longClose.FilledPrice  - position.LongEntryPrice)  * longClose.FilledQuantity;
        var shortPnl = (position.ShortEntryPrice - shortClose.FilledPrice) * shortClose.FilledQuantity;
        var pnl = longPnl + shortPnl;

        position.RealizedPnl = pnl;
        position.Status      = PositionStatus.Closed;
        position.CloseReason = reason;
        position.ClosedAt    = DateTime.UtcNow;
        _uow.Positions.Update(position);

        _uow.Alerts.Add(new Alert
        {
            UserId              = position.UserId,
            ArbitragePositionId = position.Id,
            Type                = AlertType.PositionClosed,
            Severity            = AlertSeverity.Info,
            Message             = $"Position closed: {assetSymbol} reason={reason} PnL={pnl:F4} USDC",
        });

        await _uow.SaveAsync(ct);
    }
}
