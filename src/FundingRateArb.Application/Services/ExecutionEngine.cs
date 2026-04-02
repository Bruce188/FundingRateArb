using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class ExecutionEngine : IExecutionEngine
{
    private const decimal MaxSingleOrderUsdc = 10_000m;

    private readonly IUnitOfWork _uow;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IUserSettingsService _userSettings;
    private readonly ILogger<ExecutionEngine> _logger;

    public ExecutionEngine(
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        IUserSettingsService userSettings,
        ILogger<ExecutionEngine> logger)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
        _userSettings = userSettings;
        _logger = logger;
    }

    internal static string TruncateError(string? error, int maxLength = 1900)
        => error is null ? "" : error.Length > maxLength ? error[..maxLength] + "…" : error;

    public async Task<(bool Success, string? Error)> OpenPositionAsync(
        string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc, CancellationToken ct = default)
    {
        var config = await _uow.BotConfig.GetActiveAsync();

        // B6: Absolute order size cap
        if (sizeUsdc > MaxSingleOrderUsdc)
        {
            if (_logger.IsEnabled(LogLevel.Critical))
            {
                _logger.LogCritical("Order size {Size:F2} exceeds safety cap {Max} for {Asset}",
                    sizeUsdc, MaxSingleOrderUsdc, opp.AssetSymbol);
            }
            return (false, $"Order size {sizeUsdc:F2} exceeds safety cap of {MaxSingleOrderUsdc} USDC");
        }

        // Create user-specific connectors using the user's exchange credentials
        var (longConnector, shortConnector, credError) = await CreateUserConnectorsAsync(
            userId, opp.LongExchangeName, opp.ShortExchangeName);
        if (credError is not null)
        {
            return (false, credError);
        }

        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Opening position: {Asset} Long={LongExchange} Short={ShortExchange} Size={Size} USDC",
                    opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, sizeUsdc);
            }

            // Pre-flight leverage validation: clamp to exchange maximum if configured leverage exceeds it
            var originalLeverage = config.DefaultLeverage;
            var effectiveLeverage = config.DefaultLeverage;
            try
            {
                var longMaxTask = longConnector.GetMaxLeverageAsync(opp.AssetSymbol, ct);
                var shortMaxTask = shortConnector.GetMaxLeverageAsync(opp.AssetSymbol, ct);
                await Task.WhenAll(longMaxTask, shortMaxTask);

                var longMax = longMaxTask.Result;
                var shortMax = shortMaxTask.Result;

                if (longMax.HasValue && effectiveLeverage > longMax.Value)
                {
                    _logger.LogWarning(
                        "Leverage reduced from {Configured}x to {Max}x for {Asset} on {Exchange} (exchange maximum)",
                        originalLeverage, longMax.Value, opp.AssetSymbol, opp.LongExchangeName);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LeverageReduced,
                        Severity = AlertSeverity.Warning,
                        Message = $"Leverage reduced from {originalLeverage}x to {longMax.Value}x for {opp.AssetSymbol} on {opp.LongExchangeName} (exchange maximum)",
                    });
                    effectiveLeverage = longMax.Value;
                }

                if (shortMax.HasValue && effectiveLeverage > shortMax.Value)
                {
                    _logger.LogWarning(
                        "Leverage reduced from {Configured}x to {Max}x for {Asset} on {Exchange} (exchange maximum)",
                        originalLeverage, shortMax.Value, opp.AssetSymbol, opp.ShortExchangeName);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LeverageReduced,
                        Severity = AlertSeverity.Warning,
                        Message = $"Leverage reduced from {originalLeverage}x to {shortMax.Value}x for {opp.AssetSymbol} on {opp.ShortExchangeName} (exchange maximum)",
                    });
                    effectiveLeverage = shortMax.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-flight leverage check failed for {Asset}, using configured leverage", opp.AssetSymbol);
            }

            // Pre-flight margin check: verify both exchanges have sufficient balance
            // before opening any legs. Uses leverage-adjusted margin (notional / leverage)
            // instead of full notional to correctly represent the margin needed.
            var requiredMargin = sizeUsdc / effectiveLeverage;
            try
            {
                var longBalanceTask = longConnector.GetAvailableBalanceAsync(ct);
                var shortBalanceTask = shortConnector.GetAvailableBalanceAsync(ct);
                await Task.WhenAll(longBalanceTask, shortBalanceTask);

                if (longBalanceTask.Result < requiredMargin)
                {
                    _logger.LogWarning(
                        "Pre-flight check failed: {Exchange} balance {Balance:F4} < required margin {Required:F4} for {Asset}",
                        opp.LongExchangeName, longBalanceTask.Result, requiredMargin, opp.AssetSymbol);
                    return (false, $"Insufficient margin on {opp.LongExchangeName}: available={longBalanceTask.Result:F4}, required={requiredMargin:F4}");
                }

                if (shortBalanceTask.Result < requiredMargin)
                {
                    _logger.LogWarning(
                        "Pre-flight check failed: {Exchange} balance {Balance:F4} < required margin {Required:F4} for {Asset}",
                        opp.ShortExchangeName, shortBalanceTask.Result, requiredMargin, opp.AssetSymbol);
                    return (false, $"Insufficient margin on {opp.ShortExchangeName}: available={shortBalanceTask.Result:F4}, required={requiredMargin:F4}");
                }
            }
            catch (Exception ex)
            {
                // NB3: Log full exception internally but return generic message to user
                _logger.LogWarning(ex, "Pre-flight balance check failed for {Asset}, skipping trade", opp.AssetSymbol);
                return (false, "Pre-flight balance check failed — exchange connection error");
            }

            // C-EE2: Persist a sentinel record BEFORE firing legs so that a crash after one leg
            // succeeds leaves a recoverable audit trail.
            var position = new ArbitragePosition
            {
                UserId = userId,
                AssetId = opp.AssetId,
                LongExchangeId = opp.LongExchangeId,
                ShortExchangeId = opp.ShortExchangeId,
                SizeUsdc = sizeUsdc,
                MarginUsdc = sizeUsdc,
                Leverage = effectiveLeverage,
                EntrySpreadPerHour = opp.SpreadPerHour,
                CurrentSpreadPerHour = opp.SpreadPerHour,
                Status = PositionStatus.Opening,
                OpenedAt = DateTime.UtcNow,
            };

            _uow.Positions.Add(position);
            await _uow.SaveAsync(ct);

            // B1: Use concurrent path when both connectors are reliable (no estimated fills).
            // Only use sequential when at least one leg is estimated-fill (fire-and-forget).
            OrderResultDto longResult;
            OrderResultDto shortResult;

            if (longConnector.IsEstimatedFillExchange || shortConnector.IsEstimatedFillExchange)
            {
                // NB2: Log warning when both connectors are estimated-fill
                if (longConnector.IsEstimatedFillExchange && shortConnector.IsEstimatedFillExchange)
                {
                    _logger.LogWarning(
                        "Both connectors are estimated-fill for {Asset}: Long={LongExchange}, Short={ShortExchange} — second leg will not be verified",
                        opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName);
                }

                // Sequential path: open unreliable (estimated fill) leg FIRST,
                // verify it, then open the reliable leg.
                var firstIsLong = longConnector.IsEstimatedFillExchange || !shortConnector.IsEstimatedFillExchange;
                var (firstConnector, firstSide, firstExchangeName, secondConnector, secondSide, secondExchangeName) = firstIsLong
                    ? (longConnector, Side.Long, opp.LongExchangeName, shortConnector, Side.Short, opp.ShortExchangeName)
                    : (shortConnector, Side.Short, opp.ShortExchangeName, longConnector, Side.Long, opp.LongExchangeName);

                // Open first leg (with 45-second timeout)
                OrderResultDto firstResult;
                try
                {
                    using var firstOrderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    firstOrderCts.CancelAfter(TimeSpan.FromSeconds(45));
                    firstResult = await firstConnector.PlaceMarketOrderAsync(opp.AssetSymbol, firstSide, sizeUsdc, effectiveLeverage, firstOrderCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "First leg threw for {Asset} on {Exchange}", opp.AssetSymbol, firstExchangeName);
                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Emergency close: {opp.AssetSymbol} — first leg ({firstExchangeName}) threw: {TruncateError(ex.Message)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, ex.Message);
                }

                if (!firstResult.Success)
                {
                    // First leg failed cleanly — no fees lost, just abort
                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Emergency close: {opp.AssetSymbol} — first leg ({firstExchangeName}) failed: {TruncateError(firstResult.Error)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, firstResult.Error);
                }

                // If first leg is estimated fill, verify it actually executed on-chain
                if (firstResult.IsEstimatedFill && firstConnector is IPositionVerifiable verifiable)
                {
                    var verified = await verifiable.VerifyPositionOpenedAsync(opp.AssetSymbol, firstSide, ct);
                    if (!verified)
                    {
                        _logger.LogWarning("Position verification failed for {Asset} on {Exchange} — aborting second leg",
                            opp.AssetSymbol, firstExchangeName);
                        // B3: Attempt emergency close — tx may have succeeded on-chain but verification timed out
                        await TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                        position.Status = PositionStatus.EmergencyClosed;
                        position.ClosedAt = DateTime.UtcNow;
                        _uow.Positions.Update(position);
                        _uow.Alerts.Add(new Alert
                        {
                            UserId = userId,
                            Type = AlertType.LegFailed,
                            Severity = AlertSeverity.Critical,
                            Message = $"Position verification failed on {firstExchangeName} for {opp.AssetSymbol} — tx may have failed on-chain",
                        });
                        await _uow.SaveAsync(ct);
                        return (false, $"Position verification failed on {firstExchangeName} — tx may have failed on-chain");
                    }
                }

                // First leg confirmed — now open second leg (with 45-second timeout)
                OrderResultDto secondResult;
                try
                {
                    using var secondOrderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    secondOrderCts.CancelAfter(TimeSpan.FromSeconds(45));
                    secondResult = await secondConnector.PlaceMarketOrderAsync(opp.AssetSymbol, secondSide, sizeUsdc, effectiveLeverage, secondOrderCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Second leg threw for {Asset} on {Exchange} — emergency closing first leg",
                        opp.AssetSymbol, secondExchangeName);
                    await TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Emergency close: {opp.AssetSymbol} — second leg ({secondExchangeName}) threw: {TruncateError(ex.Message)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, ex.Message);
                }

                if (!secondResult.Success)
                {
                    // Second leg failed — emergency close first leg
                    _logger.LogError(
                        "EMERGENCY CLOSE — Second leg failed: {Asset} {Exchange} Error={Error}",
                        opp.AssetSymbol, secondExchangeName, secondResult.Error);
                    await TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Emergency close: {opp.AssetSymbol} — second leg ({secondExchangeName}) failed: {TruncateError(secondResult.Error)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, secondResult.Error);
                }

                // Both legs succeeded — assign results back to long/short
                longResult = firstIsLong ? firstResult : secondResult;
                shortResult = firstIsLong ? secondResult : firstResult;
            }
            else
            {
                // Concurrent path: both connectors are reliable, use Task.WhenAll for speed (with 45-second timeout)
                using var concurrentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                concurrentCts.CancelAfter(TimeSpan.FromSeconds(45));
                var longTask = longConnector.PlaceMarketOrderAsync(opp.AssetSymbol, Side.Long, sizeUsdc, effectiveLeverage, concurrentCts.Token);
                var shortTask = shortConnector.PlaceMarketOrderAsync(opp.AssetSymbol, Side.Short, sizeUsdc, effectiveLeverage, concurrentCts.Token);

                try
                {
                    await Task.WhenAll(longTask, shortTask);
                }
                catch
                {
                    // One or both tasks threw — inspect individually below
                }

                // Handle exceptions from individual tasks
                if (longTask.IsFaulted || shortTask.IsFaulted)
                {
                    var longEx = longTask.IsFaulted ? longTask.Exception?.GetBaseException() : null;
                    var shortEx = shortTask.IsFaulted ? shortTask.Exception?.GetBaseException() : null;

                    // If only one leg threw, try to emergency close the successful one
                    if (!longTask.IsFaulted && longTask.IsCompletedSuccessfully && longTask.Result.Success)
                    {
                        await TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
                    }
                    if (!shortTask.IsFaulted && shortTask.IsCompletedSuccessfully && shortTask.Result.Success)
                    {
                        await TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
                    }

                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    var errorMsg = longEx?.Message ?? shortEx?.Message ?? "Unknown error";
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Emergency close: {opp.AssetSymbol} — concurrent leg threw: {TruncateError(errorMsg)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, errorMsg);
                }

                longResult = longTask.Result;
                shortResult = shortTask.Result;

                // Handle non-exception failures
                if (!longResult.Success || !shortResult.Success)
                {
                    // If one succeeded and the other failed, emergency close the successful one
                    if (longResult.Success && !shortResult.Success)
                    {
                        await TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
                    }
                    else if (!longResult.Success && shortResult.Success)
                    {
                        await TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
                    }

                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    var error = !longResult.Success ? longResult.Error : shortResult.Error;
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Emergency close: {opp.AssetSymbol} — leg failed: {TruncateError(error)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, error);
                }
            }

            position.LongEntryPrice = longResult.FilledPrice;
            position.ShortEntryPrice = shortResult.FilledPrice;
            position.LongOrderId = longResult.OrderId;
            position.ShortOrderId = shortResult.OrderId;
            position.Status = PositionStatus.Open;

            var longNotional = longResult.FilledPrice * longResult.FilledQuantity;
            var shortNotional = shortResult.FilledPrice * shortResult.FilledQuantity;
            position.EntryFeesUsdc = (longNotional * ExchangeFeeConstants.GetTakerFeeRate(opp.LongExchangeName))
                                   + (shortNotional * ExchangeFeeConstants.GetTakerFeeRate(opp.ShortExchangeName));

            if (longResult.IsEstimatedFill || shortResult.IsEstimatedFill)
            {
                _logger.LogWarning("Position #{Id} has estimated fill prices (Lighter) — PnL may be approximate", position.Id);
            }

            // B7: Validate fill quantities
            var longQ = longResult.FilledQuantity;
            var shortQ = shortResult.FilledQuantity;
            if (longQ > 0 && shortQ > 0)
            {
                var mismatchPct = Math.Abs(longQ - shortQ) / Math.Max(longQ, shortQ);
                if (mismatchPct > 0.05m)
                {
                    _logger.LogWarning("Fill quantity mismatch: long={LongQ}, short={ShortQ} ({Pct:P1}) for {Asset}",
                        longQ, shortQ, mismatchPct, opp.AssetSymbol);
                    position.Notes = $"Quantity mismatch: long={longQ:F6}, short={shortQ:F6} ({mismatchPct:P1})";
                }
            }

            _uow.Positions.Update(position);
            _uow.Alerts.Add(new Alert
            {
                UserId = userId,
                Type = AlertType.PositionOpened,
                Severity = AlertSeverity.Info,
                Message = $"Position opened: {opp.AssetSymbol} " +
                           $"Long/{opp.LongExchangeName} Short/{opp.ShortExchangeName} " +
                           $"@ {sizeUsdc:F2} USDC",
            });

            await _uow.SaveAsync(ct);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Position opened: {Asset} LongOrderId={LongOrderId} ShortOrderId={ShortOrderId}",
                    opp.AssetSymbol, longResult.OrderId, shortResult.OrderId);
            }

            return (true, null);

        } // end try
        finally
        {
            await DisposeConnectorAsync(longConnector);
            await DisposeConnectorAsync(shortConnector);
        }
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
        var (longConnector, shortConnector, credError) = await CreateUserConnectorsAsync(
            userId, longExchangeName, shortExchangeName);
        if (credError is not null)
        {
            _logger.LogError("Cannot close position #{PositionId} for user {UserId}: {Error}", position.Id, userId, credError);
            _uow.Alerts.Add(new Alert
            {
                UserId = userId,
                ArbitragePositionId = position.Id,
                Type = AlertType.LegFailed,
                Severity = AlertSeverity.Critical,
                Message = $"Cannot close position for {assetSymbol}: {TruncateError(credError)}. Manual intervention required.",
            });
            await _uow.SaveAsync(ct);
            return;
        }

        try
        {

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Closing position #{PositionId}: {Asset} reason={Reason}",
                    position.Id, assetSymbol, reason);
            }

            position.Status = PositionStatus.Closing;
            position.ClosingStartedAt = DateTime.UtcNow;
            _uow.Positions.Update(position);
            await _uow.SaveAsync(ct);

            // Close both legs concurrently to minimize directional exposure between fills.
            // C1: Wrap Task.WhenAll in try/catch — if either leg throws, we must handle partial
            // closure to prevent a position being stuck in Closing status forever.
            var longCloseTask = longConnector.ClosePositionAsync(assetSymbol, Side.Long, ct);
            var shortCloseTask = shortConnector.ClosePositionAsync(assetSymbol, Side.Short, ct);

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
                    // Both legs failed — mark as EmergencyClosed, operator must intervene
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
                                             $"Long error: {TruncateError(longEx?.Message, 900)}. Short error: {TruncateError(shortEx?.Message, 900)}. " +
                                             "Manual intervention required.",
                    });
                    await _uow.SaveAsync(ct);
                    return;
                }

                // Exactly one leg failed — save partial data from the successful leg,
                // leave position in Closing so the operator can manually close the remaining open leg.
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

                // Leave position.Status = Closing — do NOT mark Closed or EmergencyClosed
                _uow.Positions.Update(position);
                _uow.Alerts.Add(new Alert
                {
                    UserId = position.UserId,
                    ArbitragePositionId = position.Id,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"Close partially failed: {failedLegName} leg failed for {assetSymbol}. " +
                                          $"Error: {TruncateError(failedEx?.Message)}. Manual intervention required.",
                });
                await _uow.SaveAsync(ct);
                return;
            }

            var longClose = longCloseTask.Result;
            var shortClose = shortCloseTask.Result;

            // C2: Check Success=false BEFORE computing PnL. LighterConnector catches all exceptions
            // and returns OrderResultDto { Success = false } — the task never faults, so a try/catch
            // around WhenAll would miss it. PnL with FilledPrice=0 and FilledQuantity=0 is wrong.
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
                    // Both legs failed — mark EmergencyClosed
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
                                             $"Long={TruncateError(longCloseError, 900)} Short={TruncateError(shortCloseError, 900)}. " +
                                             "Manual intervention required.",
                    });
                }
                else
                {
                    // One leg failed — leave in Closing for manual intervention.
                    // H4: Do NOT overwrite entry prices — they must be preserved for manual PnL computation.
                    // The alert message contains the close fill data for the successful leg.
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"PARTIAL CLOSE FAILURE: {assetSymbol} " +
                                             (longClose.Success
                                                 ? $"Long=closed @ {longClose.FilledPrice:F4} "
                                                 : $"Long=FAILED: {TruncateError(longCloseError, 900)} ") +
                                             (shortClose.Success
                                                 ? $"Short=closed @ {shortClose.FilledPrice:F4}. "
                                                 : $"Short=FAILED: {TruncateError(shortCloseError, 900)}. ") +
                                             "Manual intervention required.",
                    });
                }

                await _uow.SaveAsync(ct);
                return;
            }

            // C-EE1: Compute each leg independently so differing fill quantities produce correct PnL.
            var longPnl = (longClose.FilledPrice - position.LongEntryPrice) * longClose.FilledQuantity;
            var shortPnl = (position.ShortEntryPrice - shortClose.FilledPrice) * shortClose.FilledQuantity;

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

            // Check for partial fills
            var avgEntryPrice = (position.LongEntryPrice + position.ShortEntryPrice) / 2m;
            if (avgEntryPrice > 0)
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
            position.Status = PositionStatus.Closed;
            position.CloseReason = reason;
            position.ClosedAt = DateTime.UtcNow;
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
            await DisposeConnectorAsync(longConnector);
            await DisposeConnectorAsync(shortConnector);
        }
    }

    /// <summary>
    /// Creates user-specific exchange connectors for the long and short exchanges
    /// using the user's stored API credentials.
    /// </summary>
    /// <remarks>
    /// Optimization opportunity: when the orchestrator processes N positions for the same user
    /// in one cycle, this method is called N times with redundant credential lookups and connector
    /// initializations. A per-cycle cache keyed by (userId, exchangeName) would avoid repeated work.
    /// </remarks>
    private async Task<(IExchangeConnector Long, IExchangeConnector Short, string? Error)> CreateUserConnectorsAsync(
        string userId, string longExchangeName, string shortExchangeName)
    {
        // N1: Guard against null/empty userId — legacy records or admin-initiated operations
        // could pass null, leading to silent failures in credential lookup
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError("CreateUserConnectorsAsync called with null/empty userId for {LongExchange}/{ShortExchange}",
                longExchangeName, shortExchangeName);
            return (null!, null!, "User ID is required for credential-based connector creation");
        }

        var credentials = await _userSettings.GetActiveCredentialsAsync(userId);

        var longCred = credentials.FirstOrDefault(c =>
            string.Equals(c.Exchange?.Name, longExchangeName, StringComparison.OrdinalIgnoreCase));
        var shortCred = credentials.FirstOrDefault(c =>
            string.Equals(c.Exchange?.Name, shortExchangeName, StringComparison.OrdinalIgnoreCase));

        if (longCred is null)
        {
            _logger.LogWarning("No credentials found for {Exchange} (user {UserId})", longExchangeName, userId);
            return (null!, null!, $"No credentials found for {longExchangeName}");
        }

        if (shortCred is null)
        {
            _logger.LogWarning("No credentials found for {Exchange} (user {UserId})", shortExchangeName, userId);
            return (null!, null!, $"No credentials found for {shortExchangeName}");
        }

        // NB4: Decrypt credentials in tightly scoped blocks to minimize plaintext lifetime in memory.
        // Create connectors immediately after decryption so credentials don't linger.
        IExchangeConnector? longConnector = null;
        IExchangeConnector? shortConnector = null;

        try
        {
            // Decrypt + create long connector in tight scope
            {
                var longDecrypted = DecryptAndCreateConnectorArgs(longCred, longExchangeName, userId);
                if (longDecrypted.Error is not null)
                {
                    return (null!, null!, longDecrypted.Error);
                }

                longConnector = await _connectorFactory.CreateForUserAsync(
                    longExchangeName, longDecrypted.ApiKey, longDecrypted.ApiSecret,
                    longDecrypted.WalletAddress, longDecrypted.PrivateKey,
                    longDecrypted.SubAccountAddress, longDecrypted.ApiKeyIndex);
            }

            // Decrypt + create short connector in tight scope
            {
                var shortDecrypted = DecryptAndCreateConnectorArgs(shortCred, shortExchangeName, userId);
                if (shortDecrypted.Error is not null)
                {
                    await DisposeConnectorAsync(longConnector);
                    return (null!, null!, shortDecrypted.Error);
                }

                shortConnector = await _connectorFactory.CreateForUserAsync(
                    shortExchangeName, shortDecrypted.ApiKey, shortDecrypted.ApiSecret,
                    shortDecrypted.WalletAddress, shortDecrypted.PrivateKey,
                    shortDecrypted.SubAccountAddress, shortDecrypted.ApiKeyIndex);
            }
        }
        catch (Exception ex)
        {
            // NB6: If CreateForUserAsync throws (not returns null), ensure cleanup
            _logger.LogError(ex, "Failed to create connector for user {UserId}", userId);
            await DisposeConnectorAsync(longConnector);
            await DisposeConnectorAsync(shortConnector);
            return (null!, null!, "Exchange connection failed");
        }

        if (longConnector is null)
        {
            await DisposeConnectorAsync(shortConnector);
            _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — invalid credentials", longExchangeName, userId);
            return (null!, null!, $"Could not create connector for {longExchangeName} — invalid credentials");
        }

        if (shortConnector is null)
        {
            await DisposeConnectorAsync(longConnector);
            _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — invalid credentials", shortExchangeName, userId);
            return (null!, null!, $"Could not create connector for {shortExchangeName} — invalid credentials");
        }

        return (longConnector, shortConnector, null);
    }

    /// <summary>
    /// Decrypts credential and returns the raw values needed for connector creation.
    /// Isolates decryption in its own scope to minimize plaintext credential lifetime.
    /// Note: .NET strings are immutable — decrypted credentials persist in memory until GC.
    /// This is an inherent platform limitation; SecureString is deprecated and not supported by exchange SDKs.
    /// </summary>
    private (string? ApiKey, string? ApiSecret, string? WalletAddress, string? PrivateKey, string? SubAccountAddress, string? ApiKeyIndex, string? Error) DecryptAndCreateConnectorArgs(
        UserExchangeCredential cred, string exchangeName, string userId)
    {
        try
        {
            var decrypted = _userSettings.DecryptCredential(cred);
            return (decrypted.ApiKey, decrypted.ApiSecret, decrypted.WalletAddress, decrypted.PrivateKey, decrypted.SubAccountAddress, decrypted.ApiKeyIndex, null);
        }
        catch (Exception ex)
        {
            // N2: Log only the exception type name, not the full exception which may contain cryptographic metadata
            _logger.LogError("Failed to decrypt credentials for {Exchange} (user {UserId}): {ExceptionType}",
                exchangeName, userId, ex.GetType().Name);
            return (null, null, null, null, null, null, "Credential validation failed");
        }
    }

    /// <summary>
    /// Disposes a connector, checking for IAsyncDisposable first, then IDisposable.
    /// </summary>
    private static async Task DisposeConnectorAsync(IExchangeConnector? connector)
    {
        if (connector is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    private static readonly string[] RetryableClosePatterns =
        ["no open", "not found", "position not", "does not exist", "no position",
         "timeout", "rate limit", "HTTP 429", "HTTP 503", "HTTP 502", "server error",
         "connection refused", "connection reset", "network unreachable"];

    private static bool IsRetryableCloseError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }
        return RetryableClosePatterns.Any(p => error.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private async Task TryEmergencyCloseWithRetryAsync(
        IExchangeConnector connector, string asset, Side side, string userId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        int[] backoffMs = [2000, 4000, 8000, 16000, 30000];
        var legName = side == Side.Long ? "long" : "short";

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var closeResult = await connector.ClosePositionAsync(asset, side, ct);
                if (closeResult.Success)
                {
                    return;
                }

                if (attempt < maxAttempts - 1
                    && IsRetryableCloseError(closeResult.Error))
                {
                    _logger.LogWarning(
                        "Emergency close attempt {Attempt}/{Max} failed (retryable error), retrying in {Delay}ms: {Asset} Error={Error}",
                        attempt + 1, maxAttempts, backoffMs[attempt], asset, closeResult.Error);
                    await Task.Delay(backoffMs[attempt], CancellationToken.None);
                    continue;
                }

                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    _logger.LogCritical("EMERGENCY CLOSE FAILED after {Attempts} attempts: {Asset} {Leg} Error={Error}",
                        attempt + 1, asset, legName, closeResult.Error);
                }
                _uow.Alerts.Add(new Alert
                {
                    UserId = userId,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"EMERGENCY CLOSE FAILED — {legName} leg {asset}: {TruncateError(closeResult.Error)}. Manual intervention required.",
                });
                return;
            }
            catch (Exception ex)
            {
                if (attempt < maxAttempts - 1)
                {
                    _logger.LogWarning(ex,
                        "Emergency close attempt {Attempt}/{Max} threw for {Leg} leg: {Asset}, retrying in {Delay}ms",
                        attempt + 1, maxAttempts, legName, asset, backoffMs[attempt]);
                    await Task.Delay(backoffMs[attempt], CancellationToken.None);
                    continue;
                }

                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    _logger.LogCritical(ex, "EMERGENCY CLOSE THREW for {Leg} leg: {Asset}", legName, asset);
                }
                _uow.Alerts.Add(new Alert
                {
                    UserId = userId,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"EMERGENCY CLOSE FAILED — {legName} leg {asset} threw: {TruncateError(ex.Message)}. Manual intervention required.",
                });
                return;
            }
        }
    }
}
