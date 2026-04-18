using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

/// <summary>
/// Result of checking whether both legs of a position still exist on exchanges.
/// </summary>
public enum PositionExistsResult
{
    /// <summary>Both long and short legs confirmed present.</summary>
    BothPresent,
    /// <summary>Both legs confirmed missing from exchanges.</summary>
    BothMissing,
    /// <summary>Long leg missing, short leg still present.</summary>
    LongMissing,
    /// <summary>Short leg missing, long leg still present.</summary>
    ShortMissing,
    /// <summary>Check could not be completed (API failure, timeout, etc.).</summary>
    Unknown,
}

public interface IExecutionEngine
{
    Task<(bool Success, string? Error)> OpenPositionAsync(string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc, UserConfiguration? userConfig = null, CancellationToken ct = default);
    Task ClosePositionAsync(string userId, ArbitragePosition position, CloseReason reason, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a position still exists on both exchanges.
    /// Returns true if both legs are confirmed present, false if drift detected,
    /// null if the check could not be performed (API failure).
    /// </summary>
    Task<bool?> CheckPositionExistsOnExchangesAsync(ArbitragePosition position, CancellationToken ct = default);

    /// <summary>
    /// Batch-checks whether positions still exist on exchanges, grouping by
    /// (UserId, LongExchange, ShortExchange) to reuse connectors. Returns a
    /// dictionary mapping position ID to a detailed exists result.
    /// </summary>
    Task<Dictionary<int, PositionExistsResult>> CheckPositionsExistOnExchangesBatchAsync(
        IReadOnlyList<ArbitragePosition> positions, CancellationToken ct = default);

    /// <summary>
    /// Runs the both-leg confirmation window for a position that was left in <c>Opening</c>
    /// state (e.g. after an app restart). If both legs confirm, the position is promoted to
    /// <c>Open</c> with <c>OpenConfirmedAt</c> stamped. If either leg is explicitly not-open,
    /// the position is rolled back to <c>Failed+ReconciliationDrift</c>. Indeterminate (null)
    /// results are treated the same as in the normal open flow (proceeds to Open with a Warning
    /// alert). Connectors are disposed before returning.
    /// </summary>
    Task ConfirmOrRollbackAsync(string userId, ArbitragePosition position, int timeoutSeconds, CancellationToken ct = default);
}
