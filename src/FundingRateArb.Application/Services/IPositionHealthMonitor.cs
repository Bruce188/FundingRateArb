using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IPositionHealthMonitor
{
    Task<HealthCheckResult> CheckAndActAsync(CancellationToken ct = default);

    /// <summary>
    /// Reconciles Open positions against exchanges. Marks positions as ExchangeDrift
    /// if missing from both exchanges. Called periodically by the orchestrator.
    /// </summary>
    Task ReconcileOpenPositionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of a health monitor cycle: positions that need closing, positions that were reaped,
/// and computed PnL values for each open position.
/// </summary>
public record HealthCheckResult(
    IReadOnlyList<(ArbitragePosition Position, CloseReason Reason)> ToClose,
    IReadOnlyList<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)> ReapedPositions,
    IReadOnlyDictionary<int, ComputedPositionPnl> ComputedPnl)
{
    public static readonly HealthCheckResult Empty = new(
        Array.Empty<(ArbitragePosition, CloseReason)>(),
        Array.Empty<(int, string, int, int, PositionStatus)>(),
        new Dictionary<int, ComputedPositionPnl>());
}

/// <summary>
/// PnL values computed during health check for a single position.
/// </summary>
public record ComputedPositionPnl(decimal ExchangePnl, decimal UnifiedPnl, decimal DivergencePct, decimal? CollateralImbalancePct = null);
