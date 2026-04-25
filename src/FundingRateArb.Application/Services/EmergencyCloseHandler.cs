using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class EmergencyCloseHandler : IEmergencyCloseHandler
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<EmergencyCloseHandler> _logger;

    private static readonly string[] NoPositionPatterns =
        ["no open position", "position not found", "does not exist", "no position"];

    private static readonly string[] RetryableClosePatterns =
        ["timeout", "rate limit", "HTTP 429", "HTTP 503", "HTTP 502", "server error",
         "connection refused", "connection reset", "network unreachable", "transient"];

    public EmergencyCloseHandler(IUnitOfWork uow, ILogger<EmergencyCloseHandler> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    /// <summary>
    /// Sets EntryFeesUsdc, ExitFeesUsdc, and RealizedPnl on an emergency-closed position based on the
    /// leg that filled. When <c>successfulLeg.FilledQuantity &lt;= 0</c> the method clears all three
    /// fields to zero (no trade occurred) and returns early.
    /// </summary>
    public void SetEmergencyCloseFees(
        ArbitragePosition position, OrderResultDto successfulLeg, string exchangeName)
    {
        if (successfulLeg.FilledQuantity < 0m)
        {
            _logger.LogError(
                "SetEmergencyCloseFees called with negative FilledQuantity={Qty} on position #{Id} ({Exchange}) — coercing to zero. Likely connector parsing bug.",
                successfulLeg.FilledQuantity, position.Id, exchangeName);
        }
        if (successfulLeg.FilledQuantity <= 0m)
        {
            position.EntryFeesUsdc = 0m;
            position.ExitFeesUsdc = 0m;
            position.RealizedPnl = 0m;
            return;
        }

        var legNotional = successfulLeg.FilledPrice * successfulLeg.FilledQuantity;
        var feeRate = ExchangeFeeConstants.GetTakerFeeRate(exchangeName);
        var entryFee = legNotional * feeRate;
        var exitFee = legNotional * feeRate; // emergency close at roughly the same price
        position.EntryFeesUsdc = entryFee;
        position.ExitFeesUsdc = exitFee;
        position.RealizedPnl = -(entryFee + exitFee); // net loss from fees
    }

    /// <summary>
    /// Attempts to emergency-close a position with retries. Returns true if the position
    /// was confirmed to never have existed (no circuit breaker penalty needed).
    /// </summary>
    public async Task<bool> TryEmergencyCloseWithRetryAsync(
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
                    Message = $"EMERGENCY CLOSE FAILED — {legName} leg {asset}: {ExecutionEngine.TruncateError(closeResult.Error)}. Manual intervention required.",
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
                    Message = $"EMERGENCY CLOSE FAILED — {legName} leg {asset} threw: {ExecutionEngine.TruncateError(ex.Message)}. Manual intervention required.",
                });
                return false;
            }
        }

        return false;
    }

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
}
