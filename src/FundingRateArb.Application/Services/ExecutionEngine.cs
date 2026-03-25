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

    public async Task<(bool Success, string? Error)> OpenPositionAsync(
        string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc, CancellationToken ct = default)
    {
        var config = await _uow.BotConfig.GetActiveAsync();

        // B6: Absolute order size cap
        if (sizeUsdc > MaxSingleOrderUsdc)
        {
            _logger.LogCritical("Order size {Size:F2} exceeds safety cap {Max} for {Asset}",
                sizeUsdc, MaxSingleOrderUsdc, opp.AssetSymbol);
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
            // Pre-flight margin check: verify both exchanges have sufficient balance
            // before opening any legs. Prevents the costly open→fail→emergency-close cycle
            // where one leg succeeds, the other fails on margin, and emergency close burns fees.
            var requiredMargin = sizeUsdc;
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
                _logger.LogWarning(ex, "Pre-flight balance check failed for {Asset}, skipping trade", opp.AssetSymbol);
                return (false, $"Pre-flight balance check failed: {ex.Message}");
            }

            _logger.LogInformation(
                "Opening position: {Asset} Long={LongExchange} Short={ShortExchange} Size={Size} USDC",
                opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, sizeUsdc);

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

            // Place both legs concurrently
            var longTask = longConnector.PlaceMarketOrderAsync(opp.AssetSymbol, Side.Long, sizeUsdc, effectiveLeverage, ct);
            var shortTask = shortConnector.PlaceMarketOrderAsync(opp.AssetSymbol, Side.Short, sizeUsdc, effectiveLeverage, ct);

            // C1: Wrap Task.WhenAll in try/catch — SDK connectors (Hyperliquid, Aster) throw
            // InvalidOperationException/HttpRequestException on errors. If one task faults, WhenAll
            // propagates the first exception immediately. We must inspect each task individually to
            // determine which legs succeeded and need emergency close.
            OrderResultDto? longResult = null;
            OrderResultDto? shortResult = null;
            string? longException = null;
            string? shortException = null;

            try
            {
                await Task.WhenAll(longTask, shortTask);
            }
            catch
            {
                // At least one task faulted. Inspect each task individually.
                // A completed task has a result; a faulted task has an exception.
            }

            if (longTask.IsCompletedSuccessfully)
            {
                longResult = longTask.Result;
            }
            else if (longTask.IsFaulted)
            {
                longException = longTask.Exception?.InnerException?.Message ?? longTask.Exception?.Message ?? "Unknown error";
            }

            if (shortTask.IsCompletedSuccessfully)
            {
                shortResult = shortTask.Result;
            }
            else if (shortTask.IsFaulted)
            {
                shortException = shortTask.Exception?.InnerException?.Message ?? shortTask.Exception?.Message ?? "Unknown error";
            }

            // If both legs returned results (no exceptions), use the existing Success=true/false logic
            var longSuccess = longResult?.Success == true;
            var shortSuccess = shortResult?.Success == true;

            if (longSuccess && shortSuccess)
            {
                position.LongEntryPrice = longResult!.FilledPrice;
                position.ShortEntryPrice = shortResult!.FilledPrice;
                position.LongOrderId = longResult.OrderId;
                position.ShortOrderId = shortResult.OrderId;
                position.Status = PositionStatus.Open;

                var longNotional = longResult!.FilledPrice * longResult.FilledQuantity;
                var shortNotional = shortResult!.FilledPrice * shortResult.FilledQuantity;
                position.EntryFeesUsdc = (longNotional * GetTakerFeeRate(opp.LongExchangeName))
                                       + (shortNotional * GetTakerFeeRate(opp.ShortExchangeName));

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

                _logger.LogInformation(
                    "Position opened: {Asset} LongOrderId={LongOrderId} ShortOrderId={ShortOrderId}",
                    opp.AssetSymbol, longResult.OrderId, shortResult.OrderId);

                return (true, null);
            }

            // One or both legs failed (Success=false or threw an exception)
            var longError = longException ?? longResult?.Error;
            var shortError = shortException ?? shortResult?.Error;

            _logger.LogError(
                "EMERGENCY CLOSE — One leg failed: {Asset} Long={LongStatus} Short={ShortStatus}",
                opp.AssetSymbol,
                longSuccess ? "OK" : $"FAILED: {longError}",
                shortSuccess ? "OK" : $"FAILED: {shortError}");

            // Emergency close any leg that succeeded — run sequentially to avoid concurrent exchange state issues
            if (longSuccess)
            {
                await TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
            }

            if (shortSuccess)
            {
                await TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
            }

            _uow.Alerts.Add(new Alert
            {
                UserId = userId,
                Type = AlertType.LegFailed,
                Severity = AlertSeverity.Critical,
                Message = $"Emergency close: {opp.AssetSymbol} " +
                           $"Long={longError ?? "OK"} Short={shortError ?? "OK"}",
            });

            // Mark the pre-persisted sentinel as EmergencyClosed so the position is not orphaned
            position.Status = PositionStatus.EmergencyClosed;
            _uow.Positions.Update(position);

            await _uow.SaveAsync(ct);

            var error = longError ?? shortError ?? "Both legs failed";
            return (false, error);

        } // end try
        finally
        {
            (longConnector as IDisposable)?.Dispose();
            (shortConnector as IDisposable)?.Dispose();
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
                Message = $"Cannot close position for {assetSymbol}: {credError}. Manual intervention required.",
            });
            await _uow.SaveAsync(ct);
            return;
        }

        try
        {

            _logger.LogInformation(
                "Closing position #{PositionId}: {Asset} reason={Reason}",
                position.Id, assetSymbol, reason);

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

                    _logger.LogCritical(longEx,
                        "CLOSE FAILED — long leg threw for position #{PositionId} {Asset}: {Message}",
                        position.Id, assetSymbol, longEx?.Message);
                    _logger.LogCritical(shortEx,
                        "CLOSE FAILED — short leg threw for position #{PositionId} {Asset}: {Message}",
                        position.Id, assetSymbol, shortEx?.Message);

                    position.Status = PositionStatus.EmergencyClosed;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Close failed on BOTH legs for {assetSymbol}. " +
                                             $"Long error: {longEx?.Message}. Short error: {shortEx?.Message}. " +
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

                _logger.LogCritical(failedEx,
                    "CLOSE PARTIALLY FAILED — {FailedLeg} leg threw for position #{PositionId} {Asset}: {Message}",
                    failedLegName, position.Id, assetSymbol, failedEx?.Message);

                // Leave position.Status = Closing — do NOT mark Closed or EmergencyClosed
                _uow.Positions.Update(position);
                _uow.Alerts.Add(new Alert
                {
                    UserId = position.UserId,
                    ArbitragePositionId = position.Id,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"Close partially failed: {failedLegName} leg failed for {assetSymbol}. " +
                                          $"Error: {failedEx?.Message}. Manual intervention required.",
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
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"CLOSE FAILED both legs: {assetSymbol} " +
                                             $"Long={longCloseError ?? "error"} Short={shortCloseError ?? "error"}. " +
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
                                                 : $"Long=FAILED: {longCloseError} ") +
                                             (shortClose.Success
                                                 ? $"Short=closed @ {shortClose.FilledPrice:F4}. "
                                                 : $"Short=FAILED: {shortCloseError}. ") +
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
            position.ExitFeesUsdc = (longCloseNotional * GetTakerFeeRate(longExName))
                                  + (shortCloseNotional * GetTakerFeeRate(shortExName));

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
            (longConnector as IDisposable)?.Dispose();
            (shortConnector as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Creates user-specific exchange connectors for the long and short exchanges
    /// using the user's stored API credentials.
    /// </summary>
    private async Task<(IExchangeConnector Long, IExchangeConnector Short, string? Error)> CreateUserConnectorsAsync(
        string userId, string longExchangeName, string shortExchangeName)
    {
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

        var longDecrypted = _userSettings.DecryptCredential(longCred);
        var longConnector = await _connectorFactory.CreateForUserAsync(
            longExchangeName, longDecrypted.ApiKey, longDecrypted.ApiSecret,
            longDecrypted.WalletAddress, longDecrypted.PrivateKey);

        if (longConnector is null)
        {
            _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — invalid credentials", longExchangeName, userId);
            return (null!, null!, $"Could not create connector for {longExchangeName} — invalid credentials");
        }

        var shortDecrypted = _userSettings.DecryptCredential(shortCred);
        var shortConnector = await _connectorFactory.CreateForUserAsync(
            shortExchangeName, shortDecrypted.ApiKey, shortDecrypted.ApiSecret,
            shortDecrypted.WalletAddress, shortDecrypted.PrivateKey);

        if (shortConnector is null)
        {
            (longConnector as IDisposable)?.Dispose();
            _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — invalid credentials", shortExchangeName, userId);
            return (null!, null!, $"Could not create connector for {shortExchangeName} — invalid credentials");
        }

        return (longConnector, shortConnector, null);
    }

    private static decimal GetTakerFeeRate(string exchangeName) => exchangeName switch
    {
        "Hyperliquid" => 0.00045m,
        "Lighter" => 0m,
        "Aster" => 0.0004m,
        _ => 0.0005m,
    };

    private async Task TryEmergencyCloseWithRetryAsync(
        IExchangeConnector connector, string asset, Side side, string userId, CancellationToken ct)
    {
        const int maxAttempts = 3;
        int[] backoffMs = [2000, 4000, 8000];
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
                    && closeResult.Error?.Contains("No open position") == true)
                {
                    _logger.LogWarning(
                        "Emergency close attempt {Attempt}/{Max} failed (position not settled yet), retrying in {Delay}ms: {Asset}",
                        attempt + 1, maxAttempts, backoffMs[attempt], asset);
                    await Task.Delay(backoffMs[attempt], ct);
                    continue;
                }

                _logger.LogCritical("EMERGENCY CLOSE FAILED after {Attempts} attempts: {Asset} {Leg} Error={Error}",
                    attempt + 1, asset, legName, closeResult.Error);
                _uow.Alerts.Add(new Alert
                {
                    UserId = userId,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"EMERGENCY CLOSE FAILED — {legName} leg {asset}: {closeResult.Error}. Manual intervention required.",
                });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "EMERGENCY CLOSE THREW for {Leg} leg: {Asset}", legName, asset);
                _uow.Alerts.Add(new Alert
                {
                    UserId = userId,
                    Type = AlertType.LegFailed,
                    Severity = AlertSeverity.Critical,
                    Message = $"EMERGENCY CLOSE FAILED — {legName} leg {asset} threw: {ex.Message}. Manual intervention required.",
                });
                return;
            }
        }
    }
}
