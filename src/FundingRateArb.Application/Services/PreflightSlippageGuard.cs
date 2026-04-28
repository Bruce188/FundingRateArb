using System.Collections.Concurrent;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

/// <inheritdoc cref="IPreflightSlippageGuard"/>
public class PreflightSlippageGuard : IPreflightSlippageGuard
{
    internal const decimal Baseline = 0.005m;          // 0.5 %
    internal const int HalveThreshold = 3;
    internal const int QuarterThreshold = 5;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<(string Exchange, string Asset), List<DateTime>> _reverts = new();
    private readonly Func<DateTime> _utcNow;

    public PreflightSlippageGuard() : this(() => DateTime.UtcNow) { }

    // Test seam — inject a fixed clock for deterministic tests.
    internal PreflightSlippageGuard(Func<DateTime> utcNow) => _utcNow = utcNow;

    public decimal GetCurrentCap(string exchange, string asset)
    {
        var count = CountWithinWindow(exchange, asset);
        if (count >= QuarterThreshold) return Baseline / 4m;
        if (count >= HalveThreshold) return Baseline / 2m;
        return Baseline;
    }

    public void RecordRevert(string exchange, string asset, LighterOrderRevertReason reason)
    {
        if (reason is not (LighterOrderRevertReason.Slippage or LighterOrderRevertReason.InsufficientDepth))
            return;

        var key = (exchange, asset);
        var list = _reverts.GetOrAdd(key, _ => new List<DateTime>());
        lock (list)
        {
            TrimOld(list);
            list.Add(_utcNow());
        }
    }

    public bool ShouldReject(string exchange, string asset, decimal estimatedSlippagePct)
        => Math.Abs(estimatedSlippagePct) > GetCurrentCap(exchange, asset);

    private int CountWithinWindow(string exchange, string asset)
    {
        if (!_reverts.TryGetValue((exchange, asset), out var list)) return 0;
        lock (list)
        {
            TrimOld(list);
            return list.Count;
        }
    }

    private void TrimOld(List<DateTime> list)
    {
        var cutoff = _utcNow() - Window;
        // List is appended in time order — drop the leading older entries.
        int i = 0;
        while (i < list.Count && list[i] < cutoff) i++;
        if (i > 0) list.RemoveRange(0, i);
    }
}
