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

    private readonly ConcurrentDictionary<(string Exchange, string Asset, int MaxLeverage), byte> _leverageWarned = new();

    private readonly IUnitOfWork _uow;
    private readonly IConnectorLifecycleManager _connectorLifecycle;
    private readonly IEmergencyCloseHandler _emergencyClose;
    private readonly IPositionCloser _positionCloser;
    private readonly IUserSettingsService _userSettings;
    private readonly ILeverageTierProvider _tierProvider;
    private readonly ILogger<ExecutionEngine> _logger;

    public ExecutionEngine(
        IUnitOfWork uow,
        IConnectorLifecycleManager connectorLifecycle,
        IEmergencyCloseHandler emergencyClose,
        IPositionCloser positionCloser,
        IUserSettingsService userSettings,
        ILeverageTierProvider tierProvider,
        ILogger<ExecutionEngine> logger)
    {
        _uow = uow;
        _connectorLifecycle = connectorLifecycle;
        _emergencyClose = emergencyClose;
        _positionCloser = positionCloser;
        _userSettings = userSettings;
        _tierProvider = tierProvider;
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
#pragma warning disable CA1859 // Variable type may be reassigned to DryRunConnectorWrapper
        var (longConnector, shortConnector, credError) = await _connectorLifecycle.CreateUserConnectorsAsync(
            userId, opp.LongExchangeName, opp.ShortExchangeName);
#pragma warning restore CA1859
        if (credError is not null)
        {
            return (false, credError);
        }

        // Wrap connectors for dry-run mode (simulated fills, no real orders)
        var isDryRun = config.DryRunEnabled || userConfig!.DryRunEnabled;
        if (isDryRun)
        {
            (longConnector, shortConnector) = _connectorLifecycle.WrapForDryRun(longConnector, shortConnector);
            _logger.LogInformation("[DRY-RUN] Opening position for {Asset} — simulated fills", opp.AssetSymbol);
        }

        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Opening position: {Asset} Long={LongExchange} Short={ShortExchange} Size={Size} USDC",
                    opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, sizeUsdc);
            }

            // Pre-flight leverage validation: use user leverage, apply MaxLeverageCap, then clamp to tier-based exchange max
            var originalLeverage = userConfig.DefaultLeverage > 0 ? userConfig.DefaultLeverage : config.DefaultLeverage;
            var effectiveLeverage = Math.Min(originalLeverage, config.MaxLeverageCap);
            if (effectiveLeverage < 1)
            {
                effectiveLeverage = 1;
            }
            try
            {
                // Ensure tier cache is populated for both exchanges
                await Task.WhenAll(
                    _connectorLifecycle.EnsureTiersCachedAsync(longConnector, opp.AssetSymbol, ct),
                    _connectorLifecycle.EnsureTiersCachedAsync(shortConnector, opp.AssetSymbol, ct));

                // Compute target notional for tier lookup
                var targetNotional = sizeUsdc * effectiveLeverage;

                // Get tier-based max leverage for both exchanges
                var longTierMax = _tierProvider.GetEffectiveMaxLeverage(opp.LongExchangeName, opp.AssetSymbol, targetNotional);
                var shortTierMax = _tierProvider.GetEffectiveMaxLeverage(opp.ShortExchangeName, opp.AssetSymbol, targetNotional);
                var tierMax = Math.Min(longTierMax, shortTierMax);

                // Fall back to legacy cache if tiers aren't available (tierMax == int.MaxValue)
                if (tierMax == int.MaxValue)
                {
                    var longMaxTask = _connectorLifecycle.GetCachedMaxLeverageAsync(longConnector, opp.AssetSymbol, ct);
                    var shortMaxTask = _connectorLifecycle.GetCachedMaxLeverageAsync(shortConnector, opp.AssetSymbol, ct);
                    await Task.WhenAll(longMaxTask, shortMaxTask);
                    var longMax = longMaxTask.Result ?? int.MaxValue;
                    var shortMax = shortMaxTask.Result ?? int.MaxValue;
                    tierMax = Math.Min(longMax, shortMax);
                }

                if (effectiveLeverage > tierMax)
                {
                    if (_leverageWarned.TryAdd((opp.LongExchangeName, opp.AssetSymbol, tierMax), 0))
                    {
                        _logger.LogWarning(
                            "Leverage reduced from {Configured}x to {Max}x for {Asset} (tier/cap constraint)",
                            originalLeverage, tierMax, opp.AssetSymbol);
                        _uow.Alerts.Add(new Alert
                        {
                            UserId = userId,
                            Type = AlertType.LeverageReduced,
                            Severity = AlertSeverity.Warning,
                            Message = $"Leverage reduced from {originalLeverage}x to {tierMax}x for {opp.AssetSymbol} (tier/cap constraint)",
                        });
                    }
                    effectiveLeverage = tierMax;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-flight leverage check failed for {Asset}, using configured leverage", opp.AssetSymbol);
            }

            // Pre-flight margin check + quantity coordination: fetch balances, mark prices,
            // and precision in parallel to avoid adding latency to the critical path.
            var requiredMargin = sizeUsdc;
            decimal longMarkPrice, shortMarkPrice;
            int longPrecision, shortPrecision;
            try
            {
                var longBalanceTask = longConnector.GetAvailableBalanceAsync(ct);
                var shortBalanceTask = shortConnector.GetAvailableBalanceAsync(ct);
                var longMarkPriceTask = longConnector.GetMarkPriceAsync(opp.AssetSymbol, ct);
                var shortMarkPriceTask = shortConnector.GetMarkPriceAsync(opp.AssetSymbol, ct);
                var longPrecisionTask = longConnector.GetQuantityPrecisionAsync(opp.AssetSymbol, ct);
                var shortPrecisionTask = shortConnector.GetQuantityPrecisionAsync(opp.AssetSymbol, ct);

                await Task.WhenAll(longBalanceTask, shortBalanceTask,
                    longMarkPriceTask, shortMarkPriceTask,
                    longPrecisionTask, shortPrecisionTask);

                longMarkPrice = longMarkPriceTask.Result;
                shortMarkPrice = shortMarkPriceTask.Result;
                longPrecision = longPrecisionTask.Result;
                shortPrecision = shortPrecisionTask.Result;

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

            // Compute shared target quantity for delta-neutral coordination
            var referencePrice = Math.Min(longMarkPrice, shortMarkPrice);

            // B2: Guard against zero/negative mark price before division
            if (referencePrice <= 0)
            {
                return (false, $"Mark price invalid: long={longMarkPrice}, short={shortMarkPrice}");
            }

            // NB2: Guard against pathological price divergence between exchanges
            var priceDivergence = Math.Abs(longMarkPrice - shortMarkPrice) / Math.Max(longMarkPrice, shortMarkPrice);
            if (priceDivergence > 0.02m)
            {
                return (false, $"Mark price divergence too high between exchanges ({priceDivergence:P1})");
            }

            var coarsestPrecision = Math.Min(longPrecision, shortPrecision);
            var targetQuantity = Math.Round(sizeUsdc * (decimal)effectiveLeverage / referencePrice, coarsestPrecision, MidpointRounding.ToZero);

            // Validate min notional (use highest exchange minimum — Hyperliquid = $10)
            if (targetQuantity * referencePrice < 10m)
            {
                return (false, $"Target quantity {targetQuantity} x reference price {referencePrice} = {targetQuantity * referencePrice:F2} below minimum notional");
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
                IsDryRun = isDryRun,
            };

            _uow.Positions.Add(position);
            await _uow.SaveAsync(ct);

            // B1: Use concurrent path when both connectors are reliable (no estimated fills).
            // Only use sequential when at least one leg is estimated-fill (fire-and-forget),
            // unless ForceConcurrentExecution overrides.
            OrderResultDto longResult;
            OrderResultDto shortResult;

            var useSequential = (longConnector.IsEstimatedFillExchange || shortConnector.IsEstimatedFillExchange)
                && !config.ForceConcurrentExecution;

            if (config.ForceConcurrentExecution && (longConnector.IsEstimatedFillExchange || shortConnector.IsEstimatedFillExchange))
            {
                _logger.LogWarning("ForceConcurrentExecution enabled — skipping position verification for estimated-fill exchanges");
            }

            if (useSequential)
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

                // Capture position baseline before placing order so CheckPositionExistsAsync
                // can distinguish new positions from pre-existing ones
                IReadOnlyDictionary<(string Symbol, string Side), decimal>? positionBaseline = null;
                if (firstConnector is IPositionVerifiable baselineVerifiable)
                {
                    positionBaseline = await baselineVerifiable.CapturePositionSnapshotAsync(ct);
                    if (positionBaseline is null)
                    {
                        _logger.LogWarning(
                            "Could not capture position baseline for {Asset} on {Exchange} — existence check will use no-baseline fallback",
                            opp.AssetSymbol, firstExchangeName);
                    }
                }

                // Open first leg (with 45-second timeout)
                OrderResultDto firstResult;
                try
                {
                    using var firstOrderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    firstOrderCts.CancelAfter(TimeSpan.FromSeconds(45));
                    firstResult = await firstConnector.PlaceMarketOrderByQuantityAsync(opp.AssetSymbol, firstSide, targetQuantity, effectiveLeverage, firstOrderCts.Token);
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

                        // One final read-only check before giving up — pass baseline to detect pre-existing positions
                        var positionExists = await verifiable.CheckPositionExistsAsync(opp.AssetSymbol, firstSide, positionBaseline, ct);

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
                            var neverExisted = await _emergencyClose.TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                            position.Status = neverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
                            position.ClosedAt = DateTime.UtcNow;
                            if (!neverExisted)
                            {
                                EmergencyCloseHandler.SetEmergencyCloseFees(position, firstResult, firstExchangeName);
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
                    var secondQuantity = Math.Min(targetQuantity, firstResult.FilledQuantity);
                    // Re-round to second exchange's precision
                    var secondPrecision = firstIsLong ? shortPrecision : longPrecision;
                    secondQuantity = Math.Round(secondQuantity, secondPrecision, MidpointRounding.ToZero);
                    secondResult = await secondConnector.PlaceMarketOrderByQuantityAsync(opp.AssetSymbol, secondSide, secondQuantity, effectiveLeverage, secondOrderCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Second leg threw for {Asset} on {Exchange} — emergency closing first leg",
                        opp.AssetSymbol, secondExchangeName);
                    var neverExisted = await _emergencyClose.TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                    position.Status = neverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    if (!neverExisted)
                    {
                        EmergencyCloseHandler.SetEmergencyCloseFees(position, firstResult, firstExchangeName);
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
                    var neverExisted = await _emergencyClose.TryEmergencyCloseWithRetryAsync(firstConnector, opp.AssetSymbol, firstSide, userId, ct);
                    position.Status = neverExisted ? PositionStatus.Failed : PositionStatus.EmergencyClosed;
                    position.ClosedAt = DateTime.UtcNow;
                    if (!neverExisted)
                    {
                        EmergencyCloseHandler.SetEmergencyCloseFees(position, firstResult, firstExchangeName);
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
                var longTask = longConnector.PlaceMarketOrderByQuantityAsync(opp.AssetSymbol, Side.Long, targetQuantity, effectiveLeverage, concurrentCts.Token);
                var shortTask = shortConnector.PlaceMarketOrderByQuantityAsync(opp.AssetSymbol, Side.Short, targetQuantity, effectiveLeverage, concurrentCts.Token);

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
                        var neverExistedLong = await _emergencyClose.TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
                        if (!neverExistedLong)
                        {
                            EmergencyCloseHandler.SetEmergencyCloseFees(position, longTask.Result, opp.LongExchangeName);
                            allNeverExisted = false;
                        }
                    }
                    if (!shortTask.IsFaulted && shortTask.IsCompletedSuccessfully && shortTask.Result.Success)
                    {
                        var neverExistedShort = await _emergencyClose.TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
                        if (!neverExistedShort)
                        {
                            EmergencyCloseHandler.SetEmergencyCloseFees(position, shortTask.Result, opp.ShortExchangeName);
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
                        var neverExistedLong = await _emergencyClose.TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, userId, ct);
                        if (!neverExistedLong)
                        {
                            EmergencyCloseHandler.SetEmergencyCloseFees(position, longResult, opp.LongExchangeName);
                            concurrentNeverExisted = false;
                        }
                    }
                    else if (!longResult.Success && shortResult.Success)
                    {
                        var neverExistedShort = await _emergencyClose.TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, userId, ct);
                        if (!neverExistedShort)
                        {
                            EmergencyCloseHandler.SetEmergencyCloseFees(position, shortResult, opp.ShortExchangeName);
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
            position.LongFilledQuantity = longResult.FilledQuantity;
            position.ShortFilledQuantity = shortResult.FilledQuantity;

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

            // B7: Validate fill quantities — tightened thresholds for quantity coordination
            var longQ = longResult.FilledQuantity;
            var shortQ = shortResult.FilledQuantity;
            if (longQ > 0 && shortQ > 0)
            {
                var mismatchPct = Math.Abs(longQ - shortQ) / Math.Max(longQ, shortQ);
                if (mismatchPct > 0.03m)
                {
                    _logger.LogCritical("Fill quantity mismatch CRITICAL: long={LongQ}, short={ShortQ} ({Pct:P1}) for {Asset} — quantity coordination may have failed",
                        longQ, shortQ, mismatchPct, opp.AssetSymbol);
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = userId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.QuantityMismatch,
                        Severity = AlertSeverity.Critical,
                        Message = $"Fill quantity mismatch {mismatchPct:P1}: long={longQ:F6}, short={shortQ:F6} for {opp.AssetSymbol} — coordination logic may have failed",
                    });
                    position.Notes = $"CRITICAL quantity mismatch: long={longQ:F6}, short={shortQ:F6} ({mismatchPct:P1})";
                }
                else if (mismatchPct > 0.01m)
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
            await ConnectorLifecycleManager.DisposeConnectorAsync(longConnector);
            await ConnectorLifecycleManager.DisposeConnectorAsync(shortConnector);
        }
    }

    public async Task ClosePositionAsync(string userId, ArbitragePosition position, CloseReason reason, CancellationToken ct = default)
    {
        await _positionCloser.ClosePositionAsync(userId, position, reason, ct);
    }

    public async Task<bool?> CheckPositionExistsOnExchangesAsync(ArbitragePosition position, CancellationToken ct = default)
    {
        if (position.IsDryRun)
        {
            return true; // Simulated positions always "exist"
        }

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
            var (l, s, error) = await _connectorLifecycle.CreateUserConnectorsAsync(position.UserId, longExchangeName, shortExchangeName);
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
            {
                return null;
            }

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
            await ConnectorLifecycleManager.DisposeConnectorAsync(longConnector);
            await ConnectorLifecycleManager.DisposeConnectorAsync(shortConnector);
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
                var (l, s, error) = await _connectorLifecycle.CreateUserConnectorsAsync(group.Key.UserId, group.Key.LongExchange, group.Key.ShortExchange);
                if (error is not null)
                {
                    _logger.LogWarning("Cannot reconcile positions for {UserId}/{LongExchange}/{ShortExchange}: {Error}",
                        group.Key.UserId, group.Key.LongExchange, group.Key.ShortExchange, error);
                    foreach (var p in group)
                    {
                        results[p.Id] = PositionExistsResult.Unknown;
                    }
                    continue;
                }
                longConnector = l;
                shortConnector = s;

                foreach (var pos in group)
                {
                    if (pos.IsDryRun)
                    {
                        results[pos.Id] = PositionExistsResult.BothPresent;
                        continue;
                    }

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
                await ConnectorLifecycleManager.DisposeConnectorAsync(longConnector);
                await ConnectorLifecycleManager.DisposeConnectorAsync(shortConnector);
            }
        }

        return results;
    }
}
