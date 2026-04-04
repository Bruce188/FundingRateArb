using System.Collections.Concurrent;

namespace FundingRateArb.Application.Services;

public interface IHealthMonitorState
{
    ConcurrentDictionary<int, int> NegativeFundingCycles { get; }
    ConcurrentDictionary<int, int> PriceFetchFailures { get; }
    ConcurrentDictionary<int, int> ZeroPriceCheckCounts { get; }
    int IncrementStablecoinCheckCycle();
    bool ShouldCheckStablecoin(int moduloN);
}
