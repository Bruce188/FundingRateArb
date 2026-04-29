namespace FundingRateArb.Application.Services;

/// <summary>
/// Computes per-leg slippage as a fraction: (fillPrice - intendedMid) / intendedMid.
/// Returns null when either input is missing, zero, or negative — matching the
/// AC1 "null when either is missing or unknown" semantics required for the new
/// LongEntrySlippagePct / ShortEntrySlippagePct / LongExitSlippagePct / ShortExitSlippagePct
/// columns on ArbitragePosition.
/// </summary>
public static class SlippageCalculator
{
    public static decimal? Compute(decimal? intendedMid, decimal? fillPrice)
    {
        if (intendedMid is null || fillPrice is null)
        {
            return null;
        }

        if (intendedMid.Value <= 0m || fillPrice.Value <= 0m)
        {
            return null;
        }

        return (fillPrice.Value - intendedMid.Value) / intendedMid.Value;
    }

    /// <summary>
    /// True when |slippagePct| exceeds threshold. Threshold is interpreted as a magnitude
    /// (positive value); slippage may be positive or negative depending on fill direction.
    /// Returns false when slippagePct is null.
    /// </summary>
    public static bool ExceedsThreshold(decimal? slippagePct, decimal threshold)
    {
        if (slippagePct is null)
        {
            return false;
        }

        return Math.Abs(slippagePct.Value) > threshold;
    }

    /// <summary>
    /// Walks the supplied ladder accumulating size until <paramref name="targetQuantity"/> is reached,
    /// then returns the size-weighted average fill price. Returns <c>null</c> when the cumulative size
    /// across all ladder levels is strictly less than <paramref name="targetQuantity"/>.
    /// </summary>
    /// <param name="ladder">
    /// For BUY: asks in ASCENDING price order (best ask first).
    /// For SELL: bids in DESCENDING price order (best bid first).
    /// Helper does NOT sort — caller's responsibility.
    /// </param>
    /// <param name="targetQuantity">Quantity to fill in base-asset units. Must be &gt; 0.</param>
    public static decimal? EstimatedAvgFill(IReadOnlyList<(decimal Price, decimal Size)> ladder, decimal targetQuantity)
    {
        if (ladder is null || ladder.Count == 0 || targetQuantity <= 0m)
            return null;

        decimal remaining = targetQuantity;
        decimal weighted = 0m;

        for (int i = 0; i < ladder.Count; i++)
        {
            var (price, size) = ladder[i];
            if (price <= 0m || size <= 0m) continue;

            var take = Math.Min(size, remaining);
            weighted += price * take;
            remaining -= take;

            if (remaining <= 0m)
                return weighted / targetQuantity;
        }

        return null; // cumulative size never reached targetQuantity
    }
}
