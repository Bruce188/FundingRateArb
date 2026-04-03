using System.Collections.Concurrent;
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

    // Per-asset leverage limit cache to avoid redundant API calls within a trading cycle
    private readonly ConcurrentDictionary<(string Exchange, string Asset), (int MaxLeverage, DateTime Fetched)> _leverageCache = new();
    private readonly ConcurrentDictionary<(string Exchange, string Asset, int MaxLeverage), byte> _leverageWarned = new();

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
        string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc, UserConfiguration? userConfig = null, CancellationToken ct = default)
    {
        var config = await _uow.BotConfig.GetActiveAsync();
        userConfig ??= await _userSettings.GetOrCreateConfigAsync(userId);

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

            // Pre-flight leverage validation: use user leverage (falling back to bot config), then clamp to exchange max
            var originalLeverage = userConfig.DefaultLeverage > 0 ? userConfig.DefaultLeverage : config.DefaultLeverage;
            var effectiveLeverage = originalLeverage;
            if (effectiveLeverage < 1)
            {
                effectiveLeverage = 1;
            }
            try
            {
                var longMaxTask = GetCachedMaxLeverageAsync(longConnector, opp.AssetSymbol, ct);
                var shortMaxTask = GetCachedMaxLeverageAsync(shortConnector, opp.AssetSymbol, ct);
                await Task.WhenAll(longMaxTask, shortMaxTask);

                var longMax = longMaxTask.Result;
                var shortMax = shortMaxTask.Result;

                if (longMax.HasValue && effectiveLeverage > longMax.Value)
                {
                    // Only log/alert on first occurrence per (exchange, asset, maxLeverage) tuple
                    if (_leverageWarned.TryAdd((opp.LongExchangeName, opp.AssetSymbol, longMax.Value), 0))
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
                    }
                    effectiveLeverage = longMax.Value;
                }

                if (shortMax.HasValue && effectiveLeverage > shortMax.Value)
                {
                    if (_leverageWarned.TryAdd((opp.ShortExchangeName, opp.AssetSymbol, shortMax.Value), 0))
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
                    }
                    effectiveLeverage = shortMax.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-flight leverage check failed for {Asset}, using configured leverage", opp.AssetSymbol);
            }

            // Pre-flight margin check: verify both exchanges have sufficient balance
            // before opening any legs. sizeUsdc IS the margin amount per side — connectors
            // multiply by leverage internally to get notional.
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
                    position.Status = PositionStatus.Failed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Warning,
                        Message = $"Position open failed: {opp.AssetSymbol} — first leg ({firstExchangeName}) threw: {TruncateError(ex.Message)}",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, ex.Message);
                }

                if (!firstResult.Success)
                {
                    // First leg failed cleanly — no fees lost, just abort
                    position.Status = PositionStatus.Failed;
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
                        _logger.LogWarning("Position verification failed for {Asset} on {Exchange} — performing final existence check",
                            opp.AssetSymbol, firstExchangeName);

                        // One final read-only check before giving up
                        var positionExists = await verifiable.CheckPositionExistsAsync(opp.AssetSymbol, firstSide, ct);

                        if (positionExists == true)
                        {
                            // Position opened on-chain, verification was just too slow — proceed
                            _logger.LogInformation(
                                "Position confirmed on {Exchange} after verification timeout for {Asset} — proceeding with second leg",
                                firstExchangeName, opp.AssetSymbol);
                            // Fall through to second leg logic
                        }
                        else if (positionExists == false)
                        {
                            // Position never existed on-chain
                            _logger.LogInformation(
                                "No position found on {Exchange} for {Asset} — tx likely failed on-chain",
                                firstExchangeName, opp.AssetSymbol);
                            position.Status = PositionStatus.Failed;
                            position.ClosedAt = DateTime.UtcNow;
                            _uow.Positions.Update(position);
                            _uow.Alerts.Add(new Alert
                            {
                                UserId = userId,
                                Type = AlertType.LegFailed,
                                Severity = AlertSeverity.Warning,
                                Message = $"Position open failed for {opp.AssetSymbol} on {firstExchangeName} — tx never opened on-chain",
                            });
                            await _uow.SaveAsync(ct);
                            return (false, $"Position open failed on {firstExchangeName} — tx never opened on-chain");
                        }
                        else // null — check failed, fall back to emergency close
                        {
                            _logger.LogWarning(
                                "Could not determine position state on {Exchange} for {Asset} — falling back to emergency close",
                                firstExchangeName, opp.AssetSymbol);
                            var neverExisted = await TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                            position.Status = neverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
                            position.ClosedAt = DateTime.UtcNow;
                            if (!neverExisted)
                            {
                                SetEmergencyCloseFees(position, firstResult, firstExchangeName);
                            }
                            _uow.Positions.Update(position);
                            _uow.Alerts.Add(new Alert
                            {
                                UserId = userId,
                                Type = AlertType.LegFailed,
                                Severity = neverExisted ? AlertSeverity.Warning : AlertSeverity.Critical,
                                Message = neverExisted
                                    ? $"Position open failed for {opp.AssetSymbol} on {firstExchangeName} — tx never opened on-chain"
                                    : $"Position verification failed on {firstExchangeName} for {opp.AssetSymbol} — emergency closed",
                            });
                            await _uow.SaveAsync(ct);
                            return (false, $"Position verification failed on {firstExchangeName}");
                        }
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
                    var neverExisted = await TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                    position.Status = neverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    if (!neverExisted)
                    {
                        SetEmergencyCloseFees(position, firstResult, firstExchangeName);
                    }
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
                    var neverExisted = await TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                    position.Status = neverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    if (!neverExisted)
                    {
                        SetEmergencyCloseFees(position, firstResult, firstExchangeName);
                    }
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
                    var allNeverExisted = true;
                    if (!longTask.IsFaulted && longTask.IsCompletedSuccessfully && longTask.Result.Success)
                    {
                        var neverExistedLong = await TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
                        if (!neverExistedLong)
                        {
                            SetEmergencyCloseFees(position, longTask.Result, opp.LongExchangeName);
                            allNeverExisted = false;
                        }
                    }
                    if (!shortTask.IsFaulted && shortTask.IsCompletedSuccessfully && shortTask.Result.Success)
                    {
                        var neverExistedShort = await TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
                        if (!neverExistedShort)
                        {
                            SetEmergencyCloseFees(position, shortTask.Result, opp.ShortExchangeName);
                            allNeverExisted = false;
                        }
                    }

                    position.Status = allNeverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
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
                    var concurrentNeverExisted = true;
                    if (longResult.Success && !shortResult.Success)
                    {
                        var neverExistedLong = await TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
                        if (!neverExistedLong)
                        {
                            SetEmergencyCloseFees(position, longResult, opp.LongExchangeName);
                            concurrentNeverExisted = false;
                        }
                    }
                    else if (!longResult.Success && shortResult.Success)
                    {
                        var neverExistedShort = await TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
                        if (!neverExistedShort)
                        {
                            SetEmergencyCloseFees(position, shortResult, opp.ShortExchangeName);
                            concurrentNeverExisted = false;
                        }
                    }
                    else
                    {
                        // Both legs failed — no emergency close needed, position never opened
                        concurrentNeverExisted = true;
                    }

                    position.Status = concurrentNeverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
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

            // Guard: entry prices must be non-zero before transitioning to Open
            if (position.LongEntryPrice <= 0 || position.ShortEntryPrice <= 0)
            {
                _logger.LogCritical(
                    "Position #{Id} has zero entry prices after fill: Long={LongPrice}, Short={ShortPrice}. Attempting mark price fallback.",
                    position.Id, position.LongEntryPrice, position.ShortEntryPrice);

                try
                {
                    if (position.LongEntryPrice <= 0)
                    {
                        position.LongEntryPrice = await longConnector.GetMarkPriceAsync(opp.AssetSymbol, ct);
                    }
                    if (position.ShortEntryPrice <= 0)
                    {
                        position.ShortEntryPrice = await shortConnector.GetMarkPriceAsync(opp.AssetSymbol, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Mark price fallback failed for position #{Id}", position.Id);
                }

                // If still zero after fallback, emergency-close
                if (position.LongEntryPrice <= 0 || position.ShortEntryPrice <= 0)
                {
                    position.Status = PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    _uow.Positions.Update(position);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.LegFailed,
                        Severity = AlertSeverity.Critical,
                        Message = $"Position #{position.Id} has zero entry prices (Long={position.LongEntryPrice}, Short={position.ShortEntryPrice}). Cannot compute PnL — emergency closed.",
                    });
                    await _uow.SaveAsync(ct);
                    return (false, "Zero entry prices — emergency closed");
                }
            }

            position.Status = PositionStatus.Open;

            var longNotional = position.LongEntryPrice * longResult.FilledQuantity;
            var shortNotional = position.ShortEntryPrice * shortResult.FilledQuantity;
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

            // Only update Closing status on first close attempt (not on retry)
            if (position.Status != PositionStatus.Closing)
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
                                             $"Long error: {TruncateError(longEx?.Message, 900)}. Short error: {TruncateError(shortEx?.Message, 900)}. " +
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
                                          $"Error: {TruncateError(failedEx?.Message)}. Manual intervention required.",
                });
                await _uow.SaveAsync(ct);
                return;
            }

            var longClose = longCloseTask.Result;
            var shortClose = shortCloseTask.Result;

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
                                             $"Long={TruncateError(longCloseError, 900)} Short={TruncateError(shortCloseError, 900)}. " +
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
                                                 : $"Long=FAILED: {TruncateError(longCloseError, 900)} ") +
                                             (shortClose.Success
                                                 ? $"Short=closed @ {shortClose.FilledPrice:F4}. "
                                                 : $"Short=FAILED: {TruncateError(shortCloseError, 900)}. ") +
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

    // NB4: Concurrent callers may trigger redundant fetches on cache miss — accepted
    // because duplicate writes produce the same value and the cost is bounded by the 1-hour TTL.
    private async Task<int?> GetCachedMaxLeverageAsync(IExchangeConnector connector, string asset, CancellationToken ct)
    {
        var key = (connector.ExchangeName, asset);
        if (_leverageCache.TryGetValue(key, out var cached) && cached.Fetched > DateTime.UtcNow.AddMinutes(-60))
        {
            return cached.MaxLeverage;
        }

        var maxLev = await connector.GetMaxLeverageAsync(asset, ct);
        if (maxLev.HasValue)
        {
            _leverageCache[key] = (maxLev.Value, DateTime.UtcNow);

            // NB1: Evict _leverageWarned when it grows too large.
            // Re-logging a leverage warning is harmless, so a simple clear is acceptable.
            if (_leverageWarned.Count > 500)
            {
                _leverageWarned.Clear();
            }
        }

        return maxLev;
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

    /// <summary>
    /// Sets EntryFeesUsdc and RealizedPnl on an emergency-closed position based on the
    /// fees incurred from the one leg that did open and was subsequently closed.
    /// </summary>
    private static void SetEmergencyCloseFees(
        ArbitragePosition position, OrderResultDto successfulLeg, string exchangeName)
    {
        var legNotional = successfulLeg.FilledPrice * successfulLeg.FilledQuantity;
        var feeRate = ExchangeFeeConstants.GetTakerFeeRate(exchangeName);
        var entryFee = legNotional * feeRate;
        var exitFee = legNotional * feeRate; // emergency close at roughly the same price
        position.EntryFeesUsdc = entryFee;
        position.ExitFeesUsdc = exitFee;
        position.RealizedPnl = -(entryFee + exitFee); // net loss from fees
    }

    private static readonly string[] NoPositionPatterns =
        ["no open position", "position not found", "does not exist", "no position"];

    private static readonly string[] RetryableClosePatterns =
        ["timeout", "rate limit", "HTTP 429", "HTTP 503", "HTTP 502", "server error",
         "connection refused", "connection reset", "network unreachable", "transient"];

    private static bool IsNoPositionError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }
        return NoPositionPatterns.Any(p => error.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetryableCloseError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }
        return RetryableClosePatterns.Any(p => error.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Attempts to emergency-close a position with retries. Returns true if the position
    /// was confirmed to never have existed (no circuit breaker penalty needed).
    /// </summary>
    private async Task<bool> TryEmergencyCloseWithRetryAsync(
        IExchangeConnector connector, string asset, Side side, string userId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        int[] backoffMs = [2000, 4000, 8000, 16000, 30000];
        var legName = side == Side.Long ? "long" : "short";

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var closeResult = await connector.ClosePositionAsync(asset, side, CancellationToken.None);
                if (closeResult.Success)
                {
                    return false;
                }

                // Position never existed — stop immediately, no retries needed
                if (IsNoPositionError(closeResult.Error))
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "Emergency close skipped — position never opened for {Asset} on {Leg} leg",
                            asset, legName);
                    }
                    return true;
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
                return false;
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
                return false;
            }
        }

        return false;
    }

    public async Task<bool?> CheckPositionExistsOnExchangesAsync(ArbitragePosition position, CancellationToken ct = default)
    {
        var longExchangeName = position.LongExchange?.Name;
        var shortExchangeName = position.ShortExchange?.Name;
        var assetSymbol = position.Asset?.Symbol;

        if (longExchangeName is null || shortExchangeName is null || assetSymbol is null)
        {
            _logger.LogWarning("Position #{Id} missing navigation properties for reconciliation", position.Id);
            return null;
        }

        IExchangeConnector? longConnector = null;
        IExchangeConnector? shortConnector = null;
        try
        {
            var (l, s, error) = await CreateUserConnectorsAsync(position.UserId, longExchangeName, shortExchangeName);
            if (error is not null)
            {
                _logger.LogWarning("Cannot reconcile position #{Id}: {Error}", position.Id, error);
                return null;
            }
            longConnector = l;
            shortConnector = s;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var longTask = longConnector.HasOpenPositionAsync(assetSymbol, Side.Long, cts.Token);
            var shortTask = shortConnector.HasOpenPositionAsync(assetSymbol, Side.Short, cts.Token);
            await Task.WhenAll(longTask, shortTask);

            var longExists = longTask.Result;
            var shortExists = shortTask.Result;

            // If either check failed (null), we can't confirm drift
            if (longExists is null || shortExists is null)
                return null;

            return longExists.Value && shortExists.Value;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Reconciliation timeout for position #{Id}", position.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciliation check failed for position #{Id}", position.Id);
            return null;
        }
        finally
        {
            await DisposeConnectorAsync(longConnector);
            await DisposeConnectorAsync(shortConnector);
        }
    }

    public async Task<Dictionary<int, PositionExistsResult>> CheckPositionsExistOnExchangesBatchAsync(
        IReadOnlyList<ArbitragePosition> positions, CancellationToken ct = default)
    {
        var results = new Dictionary<int, PositionExistsResult>();

        // Group positions by (UserId, LongExchangeName, ShortExchangeName) to share connectors
        var groups = positions
            .Where(p => p.LongExchange?.Name is not null && p.ShortExchange?.Name is not null && p.Asset?.Symbol is not null)
            .GroupBy(p => (p.UserId, LongExchange: p.LongExchange!.Name, ShortExchange: p.ShortExchange!.Name));

        // Mark positions with missing nav properties as Unknown
        foreach (var p in positions.Where(p => p.LongExchange?.Name is null || p.ShortExchange?.Name is null || p.Asset?.Symbol is null))
        {
            _logger.LogWarning("Position #{Id} missing navigation properties for batch reconciliation", p.Id);
            results[p.Id] = PositionExistsResult.Unknown;
        }

        foreach (var group in groups)
        {
            IExchangeConnector? longConnector = null;
            IExchangeConnector? shortConnector = null;
            try
            {
                var (l, s, error) = await CreateUserConnectorsAsync(group.Key.UserId, group.Key.LongExchange, group.Key.ShortExchange);
                if (error is not null)
                {
                    _logger.LogWarning("Cannot reconcile positions for {UserId}/{LongExchange}/{ShortExchange}: {Error}",
                        group.Key.UserId, group.Key.LongExchange, group.Key.ShortExchange, error);
                    foreach (var p in group)
                        results[p.Id] = PositionExistsResult.Unknown;
                    continue;
                }
                longConnector = l;
                shortConnector = s;

                foreach (var pos in group)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));

                        var longTask = longConnector.HasOpenPositionAsync(pos.Asset!.Symbol, Side.Long, cts.Token);
                        var shortTask = shortConnector.HasOpenPositionAsync(pos.Asset!.Symbol, Side.Short, cts.Token);
                        await Task.WhenAll(longTask, shortTask);

                        var longExists = longTask.Result;
                        var shortExists = shortTask.Result;

                        if (longExists is null || shortExists is null)
                        {
                            results[pos.Id] = PositionExistsResult.Unknown;
                        }
                        else if (longExists.Value && shortExists.Value)
                        {
                            results[pos.Id] = PositionExistsResult.BothPresent;
                        }
                        else if (!longExists.Value && !shortExists.Value)
                        {
                            results[pos.Id] = PositionExistsResult.BothMissing;
                        }
                        else if (!longExists.Value)
                        {
                            results[pos.Id] = PositionExistsResult.LongMissing;
                        }
                        else
                        {
                            results[pos.Id] = PositionExistsResult.ShortMissing;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Batch reconciliation timeout for position #{Id}", pos.Id);
                        results[pos.Id] = PositionExistsResult.Unknown;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Batch reconciliation check failed for position #{Id}", pos.Id);
                        results[pos.Id] = PositionExistsResult.Unknown;
                    }
                }
            }
            finally
            {
                await DisposeConnectorAsync(longConnector);
                await DisposeConnectorAsync(shortConnector);
            }
        }

        return results;
    }
}
