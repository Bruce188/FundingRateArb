using System.Collections.Concurrent;

namespace FundingRateArb.Application.Services;

public class HealthMonitorState : IHealthMonitorState
{
    private int _stablecoinCheckCycle = -1;

    public ConcurrentDictionary<int, int> NegativeFundingCycles { get; } = new();
    public ConcurrentDictionary<int, int> PriceFetchFailures { get; } = new();
    public ConcurrentDictionary<int, int> ZeroPriceCheckCounts { get; } = new();

    public int IncrementStablecoinCheckCycle()
        => Interlocked.Increment(ref _stablecoinCheckCycle);

    public bool ShouldCheckStablecoin(int moduloN)
        => unchecked((uint)Interlocked.Increment(ref _stablecoinCheckCycle)) % (uint)moduloN == 0;
}
