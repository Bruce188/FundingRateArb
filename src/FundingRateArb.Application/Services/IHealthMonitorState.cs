using System.Collections.Concurrent;

namespace FundingRateArb.Application.Services;

public interface IHealthMonitorState
{
    ConcurrentDictionary<int, int> NegativeFundingCycles { get; }
    ConcurrentDictionary<int, int> PriceFetchFailures { get; }
    ConcurrentDictionary<int, int> ZeroPriceCheckCounts { get; }
    bool ShouldCheckStablecoin(int moduloN);
}
