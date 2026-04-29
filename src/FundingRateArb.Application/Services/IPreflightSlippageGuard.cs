using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

/// <summary>
/// Per-<c>(exchange, asset)</c> adaptive slippage cap consulted by <c>ExecutionEngine</c>'s
/// pre-flight depth gate. State is in-memory; a process restart resets all caps to baseline
/// (analysis explicitly accepts this — restart frequency &gt;&gt; 30-min window).
/// No leg coupling: a revert on one (exchange, asset) pair never influences any other pair.
/// </summary>
public interface IPreflightSlippageGuard
{
    /// <summary>Returns the current cap for the given key (baseline / halved / quartered).</summary>
    decimal GetCurrentCap(string exchange, string asset);

    /// <summary>
    /// Records a depth-related revert for the given key. NO-OP for revert reasons that are NOT
    /// <see cref="LighterOrderRevertReason.Slippage"/> or <see cref="LighterOrderRevertReason.InsufficientDepth"/>.
    /// </summary>
    void RecordRevert(string exchange, string asset, LighterOrderRevertReason reason);

    /// <summary>True when <c>|estimatedSlippagePct|</c> exceeds the current cap.</summary>
    bool ShouldReject(string exchange, string asset, decimal estimatedSlippagePct);
}
