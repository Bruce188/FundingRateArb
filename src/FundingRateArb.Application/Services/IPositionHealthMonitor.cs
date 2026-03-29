using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IPositionHealthMonitor
{
    Task<HealthCheckResult> CheckAndActAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of a health monitor cycle: positions that need closing and positions that were reaped.
/// </summary>
public record HealthCheckResult(
    IReadOnlyList<(ArbitragePosition Position, CloseReason Reason)> ToClose,
    IReadOnlyList<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId)> ReapedPositions)
{
    public static readonly HealthCheckResult Empty = new(
        Array.Empty<(ArbitragePosition, CloseReason)>(),
        Array.Empty<(int, string, int, int)>());
}
