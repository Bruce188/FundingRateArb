using System.Collections.Concurrent;

namespace FundingRateArb.Application.Services;

public interface IHealthMonitorState
{
    ConcurrentDictionary<int, int> NegativeFundingCycles { get; }
    ConcurrentDictionary<int, int> PriceFetchFailures { get; }
    ConcurrentDictionary<int, int> ZeroPriceCheckCounts { get; }

    // Previous cycle's normalised liquidation distance per position, used by
    // PositionHealthMonitor's crossing-only refire: MarginWarning alerts fire only
    // on a downward crossing of the early-warning threshold, or on the first
    // observation if the position is already below it. Absence of a key means
    // "no prior observation" (first check after service start or reopen).
    ConcurrentDictionary<int, decimal> PrevLiquidationDistance { get; }

    bool ShouldCheckStablecoin(int moduloN);
}
