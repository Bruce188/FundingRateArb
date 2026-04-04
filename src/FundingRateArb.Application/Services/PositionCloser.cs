using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class PositionCloser : IPositionCloser
{
    private readonly IUnitOfWork _uow;
    private readonly IConnectorLifecycleManager _connectorLifecycle;
    private readonly IPnlReconciliationService _reconciliation;
    private readonly ILogger<PositionCloser> _logger;

    public PositionCloser(
        IUnitOfWork uow,
        IConnectorLifecycleManager connectorLifecycle,
        IPnlReconciliationService reconciliation,
        ILogger<PositionCloser> logger)
    {
        _uow = uow;
        _connectorLifecycle = connectorLifecycle;
        _reconciliation = reconciliation;
        _logger = logger;
    }

    public async Task ClosePositionAsync(string userId, ArbitragePosition position, CloseReason reason, CancellationToken ct = default)
    {
        // Resolve exchange names (use nav property if loaded, else query DB)
        var longExchangeName = position.LongExchange?.Name
            ?? (await _uow.Exchanges.GetByIdAsync(position.LongExchangeId))!.Name;
        var shortExchangeName = position.ShortExchange?.Name
            ?? (await _uow.Exchanges.GetByIdAsync(position.ShortExchangeId))!.Name;
        var assetSymbol = position.Asset?.Symbol
            ?? (await _uow.Assets.GetByIdAsync(position.AssetId))!.Symbol;

        // Create user-specific connectors using the user's exchange credentials
#pragma warning disable CA1859 // Variable type may be reassigned to DryRunConnectorWrapper
        var (longConnector, shortConnector, credError) = await _connectorLifecycle.CreateUserConnectorsAsync(
            userId, longExchangeName, shortExchangeName);
#pragma warning restore CA1859
        if (credError is not null)
        {
            _logger.LogError("Cannot close position #{PositionId} for user {UserId}: {Error}", position.Id, userId, credError);
            _uow.Alerts.Add(new Alert
            {
                UserId = userId,
                ArbitragePositionId = position.Id,
                Type = AlertType.LegFailed,
                Severity = AlertSeverity.Critical,
                Message = $"Cannot close position for {assetSymbol}: {ExecutionEngine.TruncateError(credError)}. Manual intervention required.",
            });
            await _uow.SaveAsync(ct);
            return;
        }

        // Wrap connectors for dry-run positions (simulated close fills)
        if (position.IsDryRun)
        {
            (longConnector, shortConnector) = _connectorLifecycle.WrapForDryRun(longConnector, shortConnector);
            _logger.LogInformation("[DRY-RUN] Closing position #{PositionId}", position.Id);
        }

        try
        {

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Closing position #{PositionId}: {Asset} reason={Reason}",
                    position.Id, assetSymbol, reason);
            }

            // Only update Closing status on first close attempt (not on retry).
            // Preserve EmergencyClosed status — it indicates exchange drift and should not be overwritten.
            if (position.Status != PositionStatus.Closing && position.Status != PositionStatus.EmergencyClosed)
            {
                position.Status = PositionStatus.Closing;
                position.ClosingStartedAt = DateTime.UtcNow;
            }
            _uow.Positions.Update(position);
            await _uow.SaveAsync(ct);

            // Skip already-closed legs on retry
            var needLongClose = !position.LongLegClosed;
            var needShortClose = !position.ShortLegClosed;

            // Both legs already closed — just finalize
            if (!needLongClose && !needShortClose)
            {
                _logger.LogInformation("Position #{PositionId} both legs already closed — finalizing", position.Id);
                await FinalizeClosedPositionAsync(position, reason, longExchangeName, shortExchangeName, assetSymbol, ct);
                return;
            }

            // Dispatch only the legs that need closing
            Task<OrderResultDto> longCloseTask;
            Task<OrderResultDto> shortCloseTask;

            if (needLongClose && needShortClose)
            {
                longCloseTask = longConnector.ClosePositionAsync(assetSymbol, Side.Long, ct);
                shortCloseTask = shortConnector.ClosePositionAsync(assetSymbol, Side.Short, ct);
            }
            else if (needLongClose)
            {
                longCloseTask = longConnector.ClosePositionAsync(assetSymbol, Side.Long, ct);
                shortCloseTask = Task.FromResult(new OrderResultDto { Success = true, FilledPrice = 0, FilledQuantity = 0 });
            }
            else
            {
                longCloseTask = Task.FromResult(new OrderResultDto { Success = true, FilledPrice = 0, FilledQuantity = 0 });
                shortCloseTask = shortConnector.ClosePositionAsync(assetSymbol, Side.Short, ct);
            }

            try
            {
                await Task.WhenAll(longCloseTask, shortCloseTask);
            }
            catch
            {
                // Inspect each leg independently to determine which failed
                var longFailed = longCloseTask.IsFaulted;
                var shortFailed = shortCloseTask.IsFaulted;

                if (longFailed && shortFailed)
                {
                    var longEx = longCloseTask.Exception?.GetBaseException();
                    var shortEx = shortCloseTask.Exception?.GetBaseException();

                    if (_logger.IsEnabled(LogLevel.Critical))
                    {
                        _logger.LogCritical(longEx,
                            "CLOSE FAILED — long leg threw for position #{PositionId} {Asset}: {Message}",
                            position.Id, assetSymbol, longEx?.Message);
                        _logger.LogCritical(shortEx,
                            "CLOSE FAILED — short leg threw for position #{PositionId} {Asset}: {Message}",
                            position.Id, assetSymbol, shortEx?.Message);
                    }

                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Close failed on BOTH legs for {assetSymbol}. " +
                                             $"Long error: {ExecutionEngine.TruncateError(longEx?.Message, 900)}. Short error: {ExecutionEngine.TruncateError(shortEx?.Message, 900)}. " +
                                             "Manual intervention required.",
                    });
                    await _uow.SaveAsync(ct);
                    return;
                }

                // Exactly one leg failed — mark the successful leg as closed, leave in Closing for retry
                if (!longFailed && needLongClose)
                {
                    var longResult = longCloseTask.IsCompletedSuccessfully ? longCloseTask.Result : null;
                    if (longResult?.Success == true)
                    {
                        position.LongLegClosed = true;
                    }
                }
                if (!shortFailed && needShortClose)
                {
                    var shortResult = shortCloseTask.IsCompletedSuccessfully ? shortCloseTask.Result : null;
                    if (shortResult?.Success == true)
                    {
                        position.ShortLegClosed = true;
                    }
                }

                var failedLegName = longFailed ? "long" : "short";
                var failedEx = longFailed
                    ? longCloseTask.Exception?.GetBaseException()
                    : shortCloseTask.Exception?.GetBaseException();

                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    _logger.LogCritical(failedEx,
                        "CLOSE PARTIALLY FAILED — {FailedLeg} leg threw for position #{PositionId} {Asset}: {Message}",
                        failedLegName, position.Id, assetSymbol, failedEx?.Message);
                }

                _uow.Positions.Update(position);
                _uow.Alerts.Add(new Alert
                {
                    UserId = position.UserId,
                    ArbitragePositionId = position.Id,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"Close partially failed: {failedLegName} leg failed for {assetSymbol}. " +
                                          $"Error: {ExecutionEngine.TruncateError(failedEx?.Message)}. Manual intervention required.",
                });
                await _uow.SaveAsync(ct);
                return;
            }

            var longClose = longCloseTask.Result;
            var shortClose = shortCloseTask.Result;

            // Dry-run wrapper returns FilledQuantity=0 (can't know position qty).
            // Override with actual stored quantities for correct PnL/fee/partial-fill calculations.
            if (position.IsDryRun)
            {
                longClose.FilledQuantity = position.LongFilledQuantity ?? 0m;
                shortClose.FilledQuantity = position.ShortFilledQuantity ?? 0m;
            }

            // Track successful legs — persist even on partial failure for retry
            if (longClose.Success && needLongClose)
            {
                position.LongLegClosed = true;
            }
            if (shortClose.Success && needShortClose)
            {
                position.ShortLegClosed = true;
            }

            if (!longClose.Success || !shortClose.Success)
            {
                var longCloseError = longClose.Success ? null : longClose.Error;
                var shortCloseError = shortClose.Success ? null : shortClose.Error;

                _logger.LogError(
                    "CLOSE FAILURE — position #{PositionId}: {Asset} Long={LongStatus} Short={ShortStatus}",
                    position.Id, assetSymbol,
                    longClose.Success ? "OK" : $"FAILED: {longCloseError}",
                    shortClose.Success ? "OK" : $"FAILED: {shortCloseError}");

                if (!longClose.Success && !shortClose.Success)
                {
                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"CLOSE FAILED both legs: {assetSymbol} " +
                                             $"Long={ExecutionEngine.TruncateError(longCloseError, 900)} Short={ExecutionEngine.TruncateError(shortCloseError, 900)}. " +
                                             "Manual intervention required.",
                    });
                }
                else
                {
                    // One leg succeeded, one failed — save leg flags and leave in Closing for retry
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"PARTIAL CLOSE FAILURE: {assetSymbol} " +
                                             (longClose.Success
                                                 ? $"Long=closed @ {longClose.FilledPrice:F4} "
                                                 : $"Long=FAILED: {ExecutionEngine.TruncateError(longCloseError, 900)} ") +
                                             (shortClose.Success
                                                 ? $"Short=closed @ {shortClose.FilledPrice:F4}. "
                                                 : $"Short=FAILED: {ExecutionEngine.TruncateError(shortCloseError, 900)}. ") +
                                             "Will retry failed leg next cycle.",
                    });
                }

                await _uow.SaveAsync(ct);
                return;
            }

            // Both legs succeeded this round — compute PnL
            // For legs that were already closed in a prior round (needLongClose=false or needShortClose=false),
            // use zero for that leg's PnL contribution since it was already accounted for.
            var longPnl = needLongClose
                ? (longClose.FilledPrice - position.LongEntryPrice) * longClose.FilledQuantity
                : 0m;
            var shortPnl = needShortClose
                ? (position.ShortEntryPrice - shortClose.FilledPrice) * shortClose.FilledQuantity
                : 0m;

            // Record exit fees
            var longCloseNotional = longClose.FilledPrice * longClose.FilledQuantity;
            var shortCloseNotional = shortClose.FilledPrice * shortClose.FilledQuantity;
            var longExName = position.LongExchange?.Name
                ?? (await _uow.Exchanges.GetByIdAsync(position.LongExchangeId))!.Name;
            var shortExName = position.ShortExchange?.Name
                ?? (await _uow.Exchanges.GetByIdAsync(position.ShortExchangeId))!.Name;
            position.ExitFeesUsdc = (longCloseNotional * ExchangeFeeConstants.GetTakerFeeRate(longExName))
                                  + (shortCloseNotional * ExchangeFeeConstants.GetTakerFeeRate(shortExName));

            // RealizedPnl = price PnL + funding collected - all fees
            var pricePnl = longPnl + shortPnl;
            var pnl = pricePnl + position.AccumulatedFunding
                     - position.EntryFeesUsdc - position.ExitFeesUsdc;

            // Check for partial fills (only for legs that were actually dispatched this round)
            var avgEntryPrice = (position.LongEntryPrice + position.ShortEntryPrice) / 2m;
            if (avgEntryPrice > 0 && needLongClose && needShortClose)
            {
                var expectedQty = position.SizeUsdc * position.Leverage / avgEntryPrice;
                var longFillRatio = expectedQty > 0 ? longClose.FilledQuantity / expectedQty : 1m;
                var shortFillRatio = expectedQty > 0 ? shortClose.FilledQuantity / expectedQty : 1m;

                if (longFillRatio < 0.95m || shortFillRatio < 0.95m)
                {
                    position.Status = PositionStatus.Closing;
                    position.Notes = $"Partial close: long={longFillRatio:P0}, short={shortFillRatio:P0}. Retry next cycle.";
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.SpreadWarning,
                        Severity = AlertSeverity.Warning,
                        Message = $"Partial close on {position.Asset?.Symbol ?? "unknown"}: long filled {longFillRatio:P0}, short filled {shortFillRatio:P0}. Position remains in Closing status.",
                    });
                    await _uow.SaveAsync(ct);
                    return;
                }
            }

            position.RealizedPnl = pnl;
            position.ClosedAt = DateTime.UtcNow;

            // Post-close PnL reconciliation (informational — never blocks close)
            try
            {
                await _reconciliation.ReconcileAsync(position, assetSymbol, longConnector, shortConnector, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PnL reconciliation failed for position #{PositionId} — continuing", position.Id);
            }

            position.Status = PositionStatus.Closed;
            position.CloseReason = reason;
            _uow.Positions.Update(position);

            _uow.Alerts.Add(new Alert
            {
                UserId = position.UserId,
                ArbitragePositionId = position.Id,
                Type = AlertType.PositionClosed,
                Severity = AlertSeverity.Info,
                Message = $"Position closed: {assetSymbol} reason={reason} PnL={pnl:F4} USDC",
            });

            await _uow.SaveAsync(ct);

        } // end try
        finally
        {
            await ConnectorLifecycleManager.DisposeConnectorAsync(longConnector);
            await ConnectorLifecycleManager.DisposeConnectorAsync(shortConnector);
        }
    }

    /// <summary>
    /// Finalizes a position where both legs are already marked as closed (retry edge case).
    /// Since leg close data was not captured in prior rounds, uses accumulated funding minus fees as PnL.
    /// </summary>
    private async Task FinalizeClosedPositionAsync(
        ArbitragePosition position, CloseReason reason,
        string longExchangeName, string shortExchangeName, string assetSymbol,
        CancellationToken ct)
    {
        // NB2: PnL here is approximate — price-based component is lost because
        // legs were closed in separate retries without capturing fill data.
        // Reconciliation skipped — connectors not available in this fallback path.
        position.RealizedPnl = position.AccumulatedFunding - position.EntryFeesUsdc - position.ExitFeesUsdc;
        position.Status = PositionStatus.Closed;
        position.CloseReason = reason;
        position.ClosedAt = DateTime.UtcNow;
        _uow.Positions.Update(position);

        _logger.LogCritical(
            "Position #{Id} finalized without price-based PnL or exit fees (legs closed in prior retries). " +
            "RealizedPnl={Pnl:F4} is approximate: funding={Funding:F4}, entryFees={EntryFees:F4}, exitFees={ExitFees:F4} (exit fees likely 0 — unrecorded)",
            position.Id, position.RealizedPnl, position.AccumulatedFunding, position.EntryFeesUsdc, position.ExitFeesUsdc);

        _uow.Alerts.Add(new Alert
        {
            UserId = position.UserId,
            ArbitragePositionId = position.Id,
            Type = AlertType.LegFailed,
            Severity = AlertSeverity.Critical,
            Message = $"Position #{position.Id} closed without price-based PnL (legs closed in separate retries). " +
                      $"RealizedPnl is approximate: {position.RealizedPnl:F2} USDC.",
        });

        _uow.Alerts.Add(new Alert
        {
            UserId = position.UserId,
            ArbitragePositionId = position.Id,
            Type = AlertType.PositionClosed,
            Severity = AlertSeverity.Info,
            Message = $"Position closed (finalized): {assetSymbol} reason={reason} PnL={position.RealizedPnl:F4} USDC",
        });

        await _uow.SaveAsync(ct);
    }
}
